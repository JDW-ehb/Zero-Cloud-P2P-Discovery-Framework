using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net.Sockets;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;
using ZCL.Repositories.Peers;

namespace ZCL.Services.FileSharing;

public sealed record SharedFileDto(
    Guid FileId,
    string Name,
    string Type,
    long Size,
    string Checksum,
    DateTime SharedSince);

/// <summary>
/// Application-level FileSharing hub.
/// Transport sessions are handled by FileSharingSessionHandler.
/// </summary>
public sealed class FileSharingService
{
    public const string Service = "FileSharing";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<string> _downloadDirectoryProvider;
    private readonly ZcspPeer _peer;

    private readonly Dictionary<Guid, string> _downloadTargets = new();
    private readonly Dictionary<Guid, SharedFileDto> _knownFiles = new();
    private readonly Dictionary<Guid, FileStream> _activeDownloads = new();
    private readonly Dictionary<Guid, long> _receivedBytes = new();

    private NetworkStream? _stream;
    private Guid _currentSessionId;
    private string? _remoteProtocolPeerId;

    private Guid _remotePeerDbId;
    private Guid _localPeerDbId;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private const int ChunkSize = 64 * 1024;
    private readonly IServiceProvider _services;

    public bool IsConnected =>
        _stream != null &&
        _currentSessionId != Guid.Empty;

    public event Action<IReadOnlyList<SharedFileDto>>? FilesReceived;
    public event Action<Guid, double>? TransferProgress;
    public event Action<Guid, string>? TransferCompleted;

    public FileSharingService(
        ZcspPeer peer,
        IServiceScopeFactory scopeFactory,
        Func<string> downloadDirectoryProvider,
        IServiceProvider services)
    {
        _peer = peer;
        _scopeFactory = scopeFactory;
        _downloadDirectoryProvider = downloadDirectoryProvider;
        _services = services;
    }



    // =========================
    // SESSION MANAGEMENT
    // =========================

    private bool IsSessionActiveWith(string protocolPeerId) =>
        _stream != null &&
        _currentSessionId != Guid.Empty &&
        _remoteProtocolPeerId == protocolPeerId;

    public async Task EnsureSessionAsync(PeerNode peer, CancellationToken ct = default)
    {
        if (IsSessionActiveWith(peer.ProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (IsSessionActiveWith(peer.ProtocolPeerId))
                return;

            await CloseCurrentSessionAsync();

            Debug.WriteLine($"[FileSharing] Connecting to {peer.ProtocolPeerId}");

            var handler = _services.GetRequiredService<FileSharingSessionHandler>();

            await _peer.ConnectAsync(
                peer.IpAddress,
                port: 5555,
                remotePeerId: peer.ProtocolPeerId,
                service: handler);

        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // =========================
    // REQUESTS (CLIENT SIDE)
    // =========================

    public async Task RequestListAsync()
    {
        if (!IsConnected)
            return;

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _currentSessionId,
            w => BinaryCodec.WriteString(w, "ListFiles"));

        await Framing.WriteAsync(_stream!, msg);
    }

    public async Task RequestFileAsync(Guid fileId)
    {
        if (!IsConnected)
            return;

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _currentSessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "RequestFile");
                w.Write(fileId.ToByteArray());
            });

        await Framing.WriteAsync(_stream!, msg);
    }

    public void SetDownloadTarget(Guid fileId, string path)
        => _downloadTargets[fileId] = path;

    public bool TryGetKnownFile(Guid id, out SharedFileDto dto)
        => _knownFiles.TryGetValue(id, out dto);

    public Task CloseCurrentSessionAsync()
    {
        try { _stream?.Dispose(); } catch { }

        _stream = null;
        _currentSessionId = Guid.Empty;
        _remoteProtocolPeerId = null;

        foreach (var fs in _activeDownloads.Values)
        {
            try { fs.Dispose(); } catch { }
        }

        _activeDownloads.Clear();
        _receivedBytes.Clear();

        return Task.CompletedTask;
    }

    // =========================
    // INTERNAL SESSION CALLBACKS
    // =========================

    internal async Task InternalOnSessionStartedAsync(
        Guid sessionId,
        string remotePeerId,
        NetworkStream stream)
    {
        Debug.WriteLine($"[FileSharing] Session started {sessionId}");

        _stream?.Dispose();
        _stream = stream;

        _currentSessionId = sessionId;
        _remoteProtocolPeerId = remotePeerId;

        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var remote = await peers.GetByProtocolPeerIdAsync(remotePeerId)
            ?? throw new InvalidOperationException($"Unknown remote peer '{remotePeerId}'");

        _remotePeerDbId = remote.PeerId;

        var localId = await peers.GetLocalPeerIdAsync()
            ?? throw new InvalidOperationException("Local peer id not found");

        _localPeerDbId = localId;

        Debug.WriteLine($"[FileSharing] Local={_localPeerDbId}, Remote={_remotePeerDbId}");
    }

    internal Task InternalOnSessionClosedAsync(Guid sessionId)
    {
        if (_currentSessionId != sessionId)
            return Task.CompletedTask;

        return CloseCurrentSessionAsync();
    }

    internal Task InternalOnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        if (_currentSessionId == Guid.Empty)
            _currentSessionId = sessionId;

        var action = BinaryCodec.ReadString(reader);

        return action switch
        {
            "ListFiles" => HandleListFilesAsync(sessionId),
            "Files" => HandleFilesResponseAsync(reader),
            "RequestFile" => HandleRequestFileAsync(sessionId, new Guid(reader.ReadBytes(16))),
            "FileChunk" => HandleFileChunkAsync(reader),
            "FileComplete" => HandleFileCompleteAsync(reader),
            _ => Task.CompletedTask
        };
    }

    // =========================
    // PROTOCOL HANDLERS
    // =========================

    private async Task HandleListFilesAsync(Guid sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == _localPeerDbId)
            .ToListAsync();

        var payload = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "Files");
                w.Write(files.Count);

                foreach (var f in files)
                {
                    w.Write(f.FileId.ToByteArray());
                    BinaryCodec.WriteString(w, f.FileName);
                    BinaryCodec.WriteString(w, f.FileType);
                    w.Write(f.FileSize);
                    BinaryCodec.WriteString(w, f.Checksum);
                    w.Write(f.SharedSince.Ticks);
                }
            });

        await Framing.WriteAsync(_stream!, payload);
    }

    private async Task HandleFilesResponseAsync(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var files = new List<SharedFileDto>(count);

        for (int i = 0; i < count; i++)
        {
            var dto = new SharedFileDto(
                new Guid(reader.ReadBytes(16)),
                BinaryCodec.ReadString(reader),
                BinaryCodec.ReadString(reader),
                reader.ReadInt64(),
                BinaryCodec.ReadString(reader),
                new DateTime(reader.ReadInt64(), DateTimeKind.Utc));

            files.Add(dto);
            _knownFiles[dto.FileId] = dto;
        }

        FilesReceived?.Invoke(files);
    }

    private async Task HandleRequestFileAsync(Guid sessionId, Guid fileId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var file = await db.SharedFiles.FirstOrDefaultAsync(f =>
            f.FileId == fileId &&
            f.PeerRefId == _localPeerDbId &&
            f.IsAvailable);

        if (file == null || !File.Exists(file.LocalPath))
            return;

        using var fs = File.OpenRead(file.LocalPath);
        var buffer = new byte[ChunkSize];

        int read;
        while ((read = await fs.ReadAsync(buffer)) > 0)
        {
            var chunk = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                sessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, "FileChunk");
                    w.Write(fileId.ToByteArray());
                    w.Write(read);
                    w.Write(buffer, 0, read);
                });

            await Framing.WriteAsync(_stream!, chunk);
        }
    }

    private Task HandleFileChunkAsync(BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));
        int length = reader.ReadInt32();
        var data = reader.ReadBytes(length);

        if (!_activeDownloads.TryGetValue(fileId, out var fs))
        {
            if (!_downloadTargets.TryGetValue(fileId, out var targetPath))
                return Task.CompletedTask;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            fs = File.Create(targetPath);
            _activeDownloads[fileId] = fs;
            _receivedBytes[fileId] = 0;
        }

        fs.Write(data);
        _receivedBytes[fileId] += data.Length;

        TransferProgress?.Invoke(fileId, _receivedBytes[fileId]);

        return Task.CompletedTask;
    }

    private Task HandleFileCompleteAsync(BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));

        if (_activeDownloads.Remove(fileId, out var fs))
            fs.Dispose();

        _receivedBytes.Remove(fileId);
        _downloadTargets.Remove(fileId);

        TransferCompleted?.Invoke(fileId, "OK");

        return Task.CompletedTask;
    }
}
