using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
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

public sealed class FileSharingService : IZcspService
{
    public string ServiceName => "FileSharing";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<string> _downloadDirectoryProvider;
    private readonly ZcspPeer _peer;

    private NetworkStream? _stream;
    private Guid _currentSessionId;
    private string? _remoteProtocolPeerId;
    private Guid _remotePeerDbId;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private const int ChunkSize = 64 * 1024;

    private readonly Dictionary<Guid, FileStream> _activeDownloads = new();
    private readonly Dictionary<Guid, long> _receivedBytes = new();

    private Guid _localPeerDbId;

    public Guid CurrentSessionId => _currentSessionId;
    public bool IsConnected =>
        _stream != null &&
        _currentSessionId != Guid.Empty;

    public event Action<IReadOnlyList<SharedFileDto>>? FilesReceived;
    public event Action<Guid, double>? TransferProgress;
    public event Action<Guid, string>? TransferCompleted;

    public FileSharingService(
        ZcspPeer peer,
        IServiceScopeFactory scopeFactory,
        Func<string> downloadDirectoryProvider)
    {
        _peer = peer;
        _scopeFactory = scopeFactory;
        _downloadDirectoryProvider = downloadDirectoryProvider;

        Debug.WriteLine("[FileSharing] Constructed");
    }

    // =========================
    // SESSION MANAGEMENT
    // =========================

    private bool IsSessionActiveWith(string protocolPeerId) =>
        _stream != null &&
        _currentSessionId != Guid.Empty &&
        _remoteProtocolPeerId == protocolPeerId;

    public async Task EnsureSessionAsync(
        PeerNode peer,
        CancellationToken ct = default)
    {
        if (IsSessionActiveWith(peer.ProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (IsSessionActiveWith(peer.ProtocolPeerId))
                return;

            await CloseCurrentSessionAsync();

            await _peer.ConnectAsync(
                peer.IpAddress,
                port: 5555,
                remotePeerId: peer.ProtocolPeerId,
                service: this);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // =========================
    // IZcspService
    // =========================

    public void BindStream(NetworkStream stream)
    {
        _stream?.Dispose();
        _stream = stream;
    }

    public async Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
    {
        _currentSessionId = sessionId;
        _remoteProtocolPeerId = remotePeerId;

        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        // remote (the other side)
        var peer = await peers.GetByProtocolPeerIdAsync(remotePeerId)
            ?? throw new InvalidOperationException($"Unknown remote peer '{remotePeerId}'");

        _remotePeerDbId = peer.PeerId;

        // local (me)
        var localId = await peers.GetLocalPeerIdAsync();
        if (localId == null)
            throw new InvalidOperationException("Local peer id not found in DB.");

        _localPeerDbId = localId.Value;
    }


    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        var action = BinaryCodec.ReadString(reader);

        switch (action)
        {
            case "ListFiles":
                await HandleListFilesAsync(sessionId);
                break;

            case "Files":
                HandleFilesResponse(reader);
                break;

            case "RequestFile":
                var fileId = new Guid(reader.ReadBytes(16));
                await HandleFileRequestAsync(sessionId, fileId);
                break;

            case "FileChunk":
                HandleFileChunk(reader);
                break;

            case "FileComplete":
                HandleFileComplete(reader);
                break;
        }
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        if (sessionId == _currentSessionId)
            return CloseCurrentSessionAsync();

        return Task.CompletedTask;
    }

    // =========================
    // REQUESTS
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

    // =========================
    // HANDLERS
    // =========================

    private async Task HandleListFilesAsync(Guid sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();


        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == _localPeerDbId && f.IsAvailable)
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


    private void HandleFilesResponse(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var files = new List<SharedFileDto>(count);

        for (int i = 0; i < count; i++)
        {
            files.Add(new SharedFileDto(
                new Guid(reader.ReadBytes(16)),
                BinaryCodec.ReadString(reader),
                BinaryCodec.ReadString(reader),
                reader.ReadInt64(),
                BinaryCodec.ReadString(reader),
                new DateTime(reader.ReadInt64(), DateTimeKind.Utc)));
        }

        FilesReceived?.Invoke(files);
    }

    private async Task HandleFileRequestAsync(Guid sessionId, Guid fileId)
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

        var done = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "FileComplete");
                w.Write(fileId.ToByteArray());
                BinaryCodec.WriteString(w, file.Checksum);
            });

        await Framing.WriteAsync(_stream!, done);
    }

    private void HandleFileChunk(BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));
        int length = reader.ReadInt32();
        var data = reader.ReadBytes(length);

        if (!_activeDownloads.TryGetValue(fileId, out var fs))
        {
            var dir = _downloadDirectoryProvider();
            Directory.CreateDirectory(dir);

            fs = File.Create(Path.Combine(dir, fileId.ToString()));
            _activeDownloads[fileId] = fs;
            _receivedBytes[fileId] = 0;
        }

        fs.Write(data);
        _receivedBytes[fileId] += data.Length;
        TransferProgress?.Invoke(fileId, _receivedBytes[fileId]);
    }

    private void HandleFileComplete(BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));
        var checksum = BinaryCodec.ReadString(reader);

        if (_activeDownloads.Remove(fileId, out var fs))
            fs.Dispose();

        _receivedBytes.Remove(fileId);
        TransferCompleted?.Invoke(fileId, checksum);
    }

    public Task CloseCurrentSessionAsync()
    {
        _stream?.Dispose();
        _stream = null;
        _currentSessionId = Guid.Empty;
        _remoteProtocolPeerId = null;

        foreach (var fs in _activeDownloads.Values)
            fs.Dispose();

        _activeDownloads.Clear();
        _receivedBytes.Clear();

        return Task.CompletedTask;
    }
    public async Task WaitForSessionBindingAsync(
    CancellationToken ct = default)
    {
        // Fast-path: already bound
        if (_remotePeerDbId != Guid.Empty)
            return;


        // Wait (bounded) for session handshake to finish
        const int maxAttempts = 40; // ~2 seconds
        for (int i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (_remotePeerDbId != Guid.Empty)
                return;


            await Task.Delay(50, ct);
        }

        throw new TimeoutException(
            "Timed out waiting for file-sharing session to bind remote peer");
    }

}
