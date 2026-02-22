using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
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

public sealed class FileSharingService : IZcspService
{
    public string ServiceName => "FileSharing";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<string> _downloadDirectoryProvider;
    private readonly ZcspPeer _peer;
    private readonly Dictionary<Guid, string> _downloadTargets = new();

    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private const int ChunkSize = 64 * 1024;

    private readonly Dictionary<Guid, SharedFileDto> _knownFiles = new();
    private readonly Dictionary<Guid, FileStream> _activeDownloads = new();
    private readonly Dictionary<Guid, long> _receivedBytes = new();
    private sealed record SessionContext(
    NetworkStream Stream,
    string RemoteProtocolPeerId,
    Guid LocalPeerDbId,
    Guid RemotePeerDbId);

    private Guid _clientSessionId = Guid.Empty;
    private string? _clientRemoteProtocolPeerId;

    private readonly ConcurrentDictionary<Guid, SessionContext> _sessions = new();
    public bool IsConnected => !_sessions.IsEmpty;

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

        Debug.WriteLine("[FileSharing] Service constructed");
    }

    // =========================
    // SESSION MANAGEMENT
    // =========================

    private bool IsSessionActiveWith(string protocolPeerId) =>
        _clientSessionId != Guid.Empty &&
        _clientRemoteProtocolPeerId == protocolPeerId &&
        _sessions.ContainsKey(_clientSessionId);

    public async Task EnsureSessionAsync(PeerNode peer, CancellationToken ct = default)
    {
        if (IsSessionActiveWith(peer.ProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (IsSessionActiveWith(peer.ProtocolPeerId))
                return;

            _clientSessionId = Guid.Empty;
            _clientRemoteProtocolPeerId = null;

            Debug.WriteLine($"[FileSharing] Connecting to {peer.ProtocolPeerId}");

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

    public async Task OnSessionStartedAsync(Guid sessionId, string remotePeerId, NetworkStream stream)
    {
        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var remote = await peers.GetByProtocolPeerIdAsync(remotePeerId)
            ?? throw new InvalidOperationException($"Unknown remote peer '{remotePeerId}'");

        var localId = await peers.GetLocalPeerIdAsync()
            ?? throw new InvalidOperationException("Local peer id not found");

        var ctx = new SessionContext(
            Stream: stream,
            RemoteProtocolPeerId: remotePeerId,
            LocalPeerDbId: localId,
            RemotePeerDbId: remote.PeerId
        );

        _sessions[sessionId] = ctx;

        _clientSessionId = sessionId;
        _clientRemoteProtocolPeerId = remotePeerId;

        Debug.WriteLine($"[FileSharing] Session started {sessionId}");
    }

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        var action = BinaryCodec.ReadString(reader);
        Debug.WriteLine($"[FileSharing] Action '{action}'");

        switch (action)
        {
            case "ListFiles":
                await HandleListFilesAsync(sessionId);
                break;

            case "Files":
                await HandleFilesResponseAsync(sessionId, reader);
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
        Debug.WriteLine($"[FileSharing] Session closed {sessionId}");
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    // =========================
    // REQUESTS (CLIENT SIDE)
    // =========================
    public async Task RequestListAsync()
    {
        if (_clientSessionId == Guid.Empty)
            return;

        if (!_sessions.TryGetValue(_clientSessionId, out var ctx))
            return;

        Debug.WriteLine("[FileSharing] Requesting file list");

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _clientSessionId,
            w => BinaryCodec.WriteString(w, "ListFiles"));

        await Framing.WriteAsync(ctx.Stream, msg);
    }

    public async Task RequestFileAsync(Guid fileId)
    {
        if (_clientSessionId == Guid.Empty)
            return;

        if (!_sessions.TryGetValue(_clientSessionId, out var ctx))
            return;

        Debug.WriteLine($"[FileSharing] Requesting file {fileId}");

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _clientSessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "RequestFile");
                w.Write(fileId.ToByteArray());
            });

        await Framing.WriteAsync(ctx.Stream, msg);
    }

    // =========================
    // HANDLERS (SERVER SIDE)
    // =========================

    private async Task HandleListFilesAsync(Guid sessionId)
    {
        Debug.WriteLine("[FileSharing] Handling ListFiles");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        if (!_sessions.TryGetValue(sessionId, out var ctx))
            return;

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == ctx.LocalPeerDbId)
            .ToListAsync();

        Debug.WriteLine($"[FileSharing] Sending {files.Count} files");

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

        await Framing.WriteAsync(ctx.Stream, payload);
    }

    private async Task HandleFilesResponseAsync(Guid sessionId, BinaryReader reader)
    {
        if (!_sessions.TryGetValue(sessionId, out var ctx))
            return;

        int count = reader.ReadInt32();
        Debug.WriteLine($"[FileSharing] Received {count} files");

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

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var existing = db.SharedFiles.Where(f => f.PeerRefId == ctx.RemotePeerDbId);
        db.SharedFiles.RemoveRange(existing);

        foreach (var dto in files)
        {
            db.SharedFiles.Add(new SharedFileEntity
            {
                FileId = dto.FileId,
                PeerRefId = ctx.RemotePeerDbId,
                FileName = dto.Name,
                FileSize = dto.Size,
                FileType = dto.Type,
                Checksum = dto.Checksum,
                LocalPath = string.Empty,
                SharedSince = dto.SharedSince,
                IsAvailable = true
            });
        }

        await db.SaveChangesAsync();

        FilesReceived?.Invoke(files);
    }


    private async Task HandleFileRequestAsync(Guid sessionId, Guid fileId)
    {
        Debug.WriteLine($"[FileSharing] Sending file {fileId}");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        if (!_sessions.TryGetValue(sessionId, out var ctx))
            return;

        var file = await db.SharedFiles.FirstOrDefaultAsync(f =>
            f.FileId == fileId &&
            f.PeerRefId == ctx.LocalPeerDbId &&
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

            await Framing.WriteAsync(ctx.Stream, chunk);
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

        if (_sessions.TryGetValue(sessionId, out var ctx2))
            await Framing.WriteAsync(ctx2.Stream, done);
    }

    private void HandleFileChunk(BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));
        int length = reader.ReadInt32();
        var data = reader.ReadBytes(length);

        if (!_activeDownloads.TryGetValue(fileId, out var fs))
        {
            if (!_downloadTargets.TryGetValue(fileId, out var targetPath))
            {
                Debug.WriteLine("[FileSharing] No download target set.");
                return;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            fs = File.Create(targetPath);


            _activeDownloads[fileId] = fs;
            _receivedBytes[fileId] = 0;

            //Debug.WriteLine($"[FileSharing] Receiving {fileName}");
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
        _downloadTargets.Remove(fileId);

        Debug.WriteLine($"[FileSharing] Completed {fileId}");
    }


    public async Task WaitForSessionBindingAsync(CancellationToken ct = default)
    {
        if (_clientSessionId != Guid.Empty && _sessions.ContainsKey(_clientSessionId))
            return;

        for (int i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (_clientSessionId != Guid.Empty && _sessions.ContainsKey(_clientSessionId))
                return;

            await Task.Delay(50, ct);
        }

        throw new TimeoutException("FileSharing session bind timeout");
    }

    public void SetDownloadTarget(Guid fileId, string path)
    {
        _downloadTargets[fileId] = path;
    }
    public bool TryGetKnownFile(Guid id, out SharedFileDto dto)
    => _knownFiles.TryGetValue(id, out dto);

}
