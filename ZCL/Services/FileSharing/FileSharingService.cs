using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using ZCL.API;
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
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly RoutingState _routingState;

    private sealed record UploadState(
    string OwnerProtocolPeerId,
    Guid OwnerPeerDbId,
    Guid FileId,
    string FileName,
    string FileType,
    long FileSize,
    string Checksum,
    DateTime SharedSinceUtc,
    FileStream Stream,
    string TargetPath);

    private readonly ConcurrentDictionary<Guid, UploadState> _uploads = new();
    private const int ChunkSize = 64 * 1024;

    private readonly ConcurrentDictionary<Guid, string> _downloadTargets = new();
    private readonly ConcurrentDictionary<Guid, SharedFileDto> _knownFiles = new();
    private readonly ConcurrentDictionary<Guid, FileStream> _activeDownloads = new();
    private readonly ConcurrentDictionary<Guid, long> _receivedBytes = new();
    private readonly ConcurrentDictionary<string, Task> _connectInFlight = new();
    private readonly ConcurrentDictionary<string, DateTime> _nextAllowedConnectUtc = new();


    private sealed record SessionContext(
    Stream Stream,
    string RemoteProtocolPeerId,
    Guid LocalPeerDbId,
    Guid RemotePeerDbId);

    private Guid? _serverSessionId;
    private readonly ConcurrentDictionary<string, Guid> _directSessions = new();

    private readonly ConcurrentDictionary<Guid, SessionContext> _sessions = new();
    public bool IsConnected => !_sessions.IsEmpty;

    public event Action<IReadOnlyList<SharedFileDto>>? FilesReceived;
    public event Action<Guid, double>? TransferProgress;
    public event Action<Guid, string>? TransferCompleted;

    public FileSharingService(
        ZcspPeer peer,
        IServiceScopeFactory scopeFactory,
        Func<string> downloadDirectoryProvider,
        RoutingState routingState)
    {
        _peer = peer;
        _scopeFactory = scopeFactory;
        _downloadDirectoryProvider = downloadDirectoryProvider;
        _routingState = routingState;

        Debug.WriteLine("[FileSharing] Service constructed");
    }


    private async Task<(Guid sessionId, SessionContext ctx, bool viaServer)>
    GetRouteAsync(PeerNode? directPeer, CancellationToken ct)
    {
        if (_routingState.Mode == RoutingMode.ViaServer)
        {
            if (_serverSessionId is Guid sid &&
                _sessions.TryGetValue(sid, out var serverCtx))
            {
                return (sid, serverCtx, true);
            }

            if (await EnsureServerSessionAsync(ct) &&
                _serverSessionId is Guid sid2 &&
                _sessions.TryGetValue(sid2, out var serverCtx2))
            {
                return (sid2, serverCtx2, true);
            }
        }

        if (directPeer == null)
            throw new InvalidOperationException("No server and no direct peer.");

        if (_directSessions.TryGetValue(directPeer.ProtocolPeerId, out var directSid) &&
            _sessions.TryGetValue(directSid, out var directCtx))
        {
            return (directSid, directCtx, false);
        }

        await EnsureSessionAsync(directPeer, ct);

        if (_directSessions.TryGetValue(directPeer.ProtocolPeerId, out var newSid) &&
            _sessions.TryGetValue(newSid, out var newCtx))
        {
            return (newSid, newCtx, false);
        }

        throw new InvalidOperationException("Direct session failed.");
    }

    public async Task RequestListRoutedAsync(
    PeerNode? ownerPeer = null,
    string? targetProtocolPeerId = null,
    CancellationToken ct = default)
    {

        var (sid, ctx, viaServer) = await GetRouteAsync(ownerPeer, ct);

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sid,
            w =>
            {
                if (viaServer && !string.IsNullOrWhiteSpace(targetProtocolPeerId))
                {
                    BinaryCodec.WriteString(w, "ListFilesFor");
                    BinaryCodec.WriteString(w, targetProtocolPeerId);
                }
                else
                {
                    BinaryCodec.WriteString(w, "ListFiles");
                }
            });

        await Framing.WriteAsync(ctx.Stream, msg);
    }

    public async Task EnsureSessionAsync(PeerNode peer, CancellationToken ct = default)
    {
        if (_directSessions.ContainsKey(peer.ProtocolPeerId))
            return;

        // Basic backoff gate (prevents retry storms)
        if (_nextAllowedConnectUtc.TryGetValue(peer.ProtocolPeerId, out var next) &&
            DateTime.UtcNow < next)
            return;

        // Single-flight: if a connect is already running for this peer, await it.
        var task = _connectInFlight.GetOrAdd(peer.ProtocolPeerId, _ => ConnectOnceAsync(peer, ct));
        try
        {
            await task;
        }
        finally
        {
            // Remove only if this exact task is still registered
            _connectInFlight.TryRemove(new KeyValuePair<string, Task>(peer.ProtocolPeerId, task));
        }
    }

    private async Task ConnectOnceAsync(PeerNode peer, CancellationToken ct)
    {
        // Double-check after awaiting other connect
        if (_directSessions.ContainsKey(peer.ProtocolPeerId))
            return;

        Debug.WriteLine($"[FileSharing] Connecting to {peer.ProtocolPeerId}");

        try
        {
            await _peer.ConnectAsync(peer.IpAddress, 5555, peer.ProtocolPeerId, this, ct);
        }
        catch (SocketException ex)
        {
            // Exponential-ish backoff (simple, effective)
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(2);
            Debug.WriteLine($"[FileSharing] Connect failed (SocketException): {ex.Message}");
            throw;
        }
        catch (IOException ex)
        {
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(2);
            Debug.WriteLine($"[FileSharing] Connect failed (IO): {ex.Message}");
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Auth failures should backoff longer (no point spamming)
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(10);
            Debug.WriteLine($"[FileSharing] Connect failed (Unauthorized): {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(3);
            Debug.WriteLine($"[FileSharing] Connect failed: {ex.Message}");
            throw;
        }
    }


    public async Task OnSessionStartedAsync(Guid sessionId, string remotePeerId, Stream stream)
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

        if (_routingState.Mode == RoutingMode.ViaServer &&
            remotePeerId == _routingState.ServerProtocolPeerId)
        {
            _serverSessionId = sessionId;
        }
        else
        {
            _directSessions[remotePeerId] = sessionId;
        }

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
                {
                    var requestFileId = new Guid(reader.ReadBytes(16));
                    await HandleFileRequestAsync(sessionId, requestFileId);
                    break;
                }

            case "FileChunk":
                HandleFileChunk(reader);
                break;

            case "FileComplete":
                HandleFileComplete(reader);
                break;

            case "ListFilesFor":
                {
                    var targetProtocolPeerId = BinaryCodec.ReadString(reader);
                    await HandleListFilesForAsync(sessionId, targetProtocolPeerId);
                    break;
                }
            case "RequestFileFor":
                {
                    var targetProtocolPeerId = BinaryCodec.ReadString(reader);
                    var fileId = new Guid(reader.ReadBytes(16));
                    await HandleFileRequestForAsync(sessionId, targetProtocolPeerId, fileId);
                    break;
                }
            case "UploadStart":
                await HandleUploadStartAsync(sessionId, reader);
                break;
            case "UploadChunk":
                await HandleUploadChunkAsync(sessionId, reader);
                break;
            case "UploadComplete":
                await HandleUploadCompleteAsync(sessionId, reader);
                break;
        }
    }

    public async Task OnSessionClosedAsync(Guid sessionId)
    {
        Debug.WriteLine($"[FileSharing] Session closed {sessionId}");

        if (!_sessions.TryRemove(sessionId, out var ctx))
            return;

        bool wasServer =
            _routingState.Mode == RoutingMode.ViaServer &&
            ctx.RemoteProtocolPeerId == _routingState.ServerProtocolPeerId;

        if (_serverSessionId == sessionId)
            _serverSessionId = null;

        var direct = _directSessions
            .FirstOrDefault(x => x.Value == sessionId);

        if (!string.IsNullOrEmpty(direct.Key))
            _directSessions.TryRemove(direct.Key, out _);

        if (wasServer)
        {
            Debug.WriteLine("[FileSharing] Server lost. Switching to Direct.");
            _routingState.SetDirect();

            var peersToReconnect = _sessions
                .Values
                .Select(s => s.RemoteProtocolPeerId)
                .Distinct()
                .ToList();

            foreach (var peerId in peersToReconnect)
            {
                _ = EnsureDirectReconnectAsync(peerId);
            }
        }

        await Task.CompletedTask;
    }
    private async Task EnsureDirectReconnectAsync(string protocolPeerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var peer = await peers.GetByProtocolPeerIdAsync(protocolPeerId);
        if (peer == null)
            return;

        try
        {
            await EnsureSessionAsync(peer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileSharing] Direct reconnect failed: {ex.Message}");
        }
    }

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
                    w.Write(f.RemoteFileId.ToByteArray());
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
                Id = Guid.NewGuid(),
                RemoteFileId = dto.FileId,
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
            f.RemoteFileId == fileId &&
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

            if (!_activeDownloads.TryAdd(fileId, fs))
            {
                fs.Dispose();
                if (!_activeDownloads.TryGetValue(fileId, out fs))
                    return;
            }

            _receivedBytes.TryAdd(fileId, 0);
        }

        fs.Write(data, 0, data.Length);

        var total = _receivedBytes.AddOrUpdate(fileId, data.Length, (_, old) => old + data.Length);

        double progress = 0;

        if (_knownFiles.TryGetValue(fileId, out var dto) && dto.Size > 0)
            progress = (double)total / dto.Size * 100.0;

        TransferProgress?.Invoke(fileId, progress);
    }

    private void HandleFileComplete(BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));
        var checksum = BinaryCodec.ReadString(reader);

        if (_activeDownloads.TryRemove(fileId, out var fs))
            fs.Dispose();

        _receivedBytes.TryRemove(fileId, out _);
        _downloadTargets.TryRemove(fileId, out _);

        TransferCompleted?.Invoke(fileId, checksum);

        Debug.WriteLine($"[FileSharing] Completed {fileId}");
    }

    private async Task HandleListFilesForAsync(Guid sessionId, string targetProtocolPeerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        if (!_sessions.TryGetValue(sessionId, out var ctx))
            return;

        var target = await peers.GetByProtocolPeerIdAsync(targetProtocolPeerId);
        if (target == null) return;

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == target.PeerId)
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
                    w.Write(f.RemoteFileId.ToByteArray());
                    BinaryCodec.WriteString(w, f.FileName);
                    BinaryCodec.WriteString(w, f.FileType);
                    w.Write(f.FileSize);
                    BinaryCodec.WriteString(w, f.Checksum);
                    w.Write(f.SharedSince.Ticks);
                }
            });

        await Framing.WriteAsync(ctx.Stream, payload);
    }
    private async Task HandleFileRequestForAsync(Guid sessionId, string targetProtocolPeerId, Guid fileId)
    {
        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        if (!_sessions.TryGetValue(sessionId, out var ctx))
            return;

        var target = await peers.GetByProtocolPeerIdAsync(targetProtocolPeerId);
        if (target == null) return;

        var file = await db.SharedFiles.FirstOrDefaultAsync(f =>
            f.RemoteFileId == fileId &&
            f.PeerRefId == target.PeerId &&
            f.IsAvailable);

        if (file == null || string.IsNullOrWhiteSpace(file.LocalPath) || !File.Exists(file.LocalPath))
            return;

        await StreamFileAsync(ctx.Stream, sessionId, fileId, file.LocalPath, file.Checksum);
    }

    private async Task StreamFileAsync(Stream stream, Guid sessionId, Guid fileId, string path, string checksum)
    {
        using var fs = File.OpenRead(path);
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

            await Framing.WriteAsync(stream, chunk);
        }

        var done = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "FileComplete");
                w.Write(fileId.ToByteArray());
                BinaryCodec.WriteString(w, checksum);
            });

        await Framing.WriteAsync(stream, done);
    }

    public async Task<bool> EnsureServerSessionAsync(CancellationToken ct = default)
    {
        if (_routingState.Mode != RoutingMode.ViaServer)
            return false;

        if (_serverSessionId is Guid sid && _sessions.ContainsKey(sid))
            return true;

        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var server = await peers.GetByProtocolPeerIdAsync(
            _routingState.ServerProtocolPeerId!);

        if (server == null)
            return false;

        try
        {
            await _peer.ConnectAsync(
                server.IpAddress,
                5555,
                server.ProtocolPeerId,
                this);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task MirrorUploadToServerAsync(
    Guid fileId,
    string ownerProtocolPeerId,
    string fileName,
    string fileType,
    long fileSize,
    string checksum,
    DateTime sharedSinceUtc,
    string localPath,
    CancellationToken ct = default)
    {
        if (!await EnsureServerSessionAsync(ct))
            return;

        if (_serverSessionId is not Guid sid)
            return;

        if (!_sessions.TryGetValue(sid, out var ctx))
            return;

        var start = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sid,
            w =>
            {
                BinaryCodec.WriteString(w, "UploadStart");
                BinaryCodec.WriteString(w, ownerProtocolPeerId);
                w.Write(fileId.ToByteArray());
                BinaryCodec.WriteString(w, fileName);
                BinaryCodec.WriteString(w, fileType);
                w.Write(fileSize);
                BinaryCodec.WriteString(w, checksum);
                w.Write(sharedSinceUtc.Ticks);
            });

        await Framing.WriteAsync(ctx.Stream, start);

        using var fs = File.OpenRead(localPath);
        var buffer = new byte[ChunkSize];
        int read;

        while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            var chunk = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                sid,
                w =>
                {
                    BinaryCodec.WriteString(w, "UploadChunk");
                    w.Write(fileId.ToByteArray());
                    w.Write(read);
                    w.Write(buffer, 0, read);
                });

            await Framing.WriteAsync(ctx.Stream, chunk);
        }

        var done = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sid,
            w =>
            {
                BinaryCodec.WriteString(w, "UploadComplete");
                w.Write(fileId.ToByteArray());
                BinaryCodec.WriteString(w, checksum);
            });

        await Framing.WriteAsync(ctx.Stream, done);
    }

    private async Task HandleUploadStartAsync(Guid sessionId, BinaryReader reader)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
        var peersRepo = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var ownerProtocolPeerId = BinaryCodec.ReadString(reader);
        var fileId = new Guid(reader.ReadBytes(16));
        var name = BinaryCodec.ReadString(reader);
        var type = BinaryCodec.ReadString(reader);
        var size = reader.ReadInt64();
        var checksum = BinaryCodec.ReadString(reader);
        var sharedSince = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);

        var owner = await peersRepo.GetByProtocolPeerIdAsync(ownerProtocolPeerId);

        if (owner == null)
        {
            owner = new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = ownerProtocolPeerId,
                HostName = ownerProtocolPeerId,
                IpAddress = "server-mirrored",
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                IsLocal = false,
                OnlineStatus = PeerOnlineStatus.Online
            };

            db.PeerNodes.Add(owner);
            await db.SaveChangesAsync();
        }

        var mirrorDir = Path.Combine(AppContext.BaseDirectory, "mirror", ownerProtocolPeerId);
        Directory.CreateDirectory(mirrorDir);

        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var targetPath = Path.Combine(mirrorDir, $"{fileId:N}_{safeName}");

        var fs = File.Create(targetPath);

        _uploads[fileId] = new UploadState(
            OwnerProtocolPeerId: ownerProtocolPeerId,
            OwnerPeerDbId: owner.PeerId,
            FileId: fileId,
            FileName: name,
            FileType: type,
            FileSize: size,
            Checksum: checksum,
            SharedSinceUtc: sharedSince,
            Stream: fs,
            TargetPath: targetPath);
    }

    private Task HandleUploadChunkAsync(Guid sessionId, BinaryReader reader)
    {
        var fileId = new Guid(reader.ReadBytes(16));
        var length = reader.ReadInt32();
        var data = reader.ReadBytes(length);

        if (_uploads.TryGetValue(fileId, out var up))
            up.Stream.Write(data, 0, data.Length);

        return Task.CompletedTask;
    }

    private async Task HandleUploadCompleteAsync(Guid sessionId, BinaryReader reader)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var fileId = new Guid(reader.ReadBytes(16));
        var checksum = BinaryCodec.ReadString(reader);

        if (!_uploads.TryRemove(fileId, out var up))
            return;

        up.Stream.Dispose();

        var existing = await db.SharedFiles.FirstOrDefaultAsync(f =>
            f.RemoteFileId == fileId && f.PeerRefId == up.OwnerPeerDbId);

        if (existing == null)
        {
            db.SharedFiles.Add(new SharedFileEntity
            {
                Id = Guid.NewGuid(),
                RemoteFileId = up.FileId,
                PeerRefId = up.OwnerPeerDbId,
                FileName = up.FileName,
                FileSize = up.FileSize,
                FileType = up.FileType,
                Checksum = up.Checksum,
                LocalPath = up.TargetPath,
                SharedSince = up.SharedSinceUtc,
                IsAvailable = true
            });
        }
        else
        {
            existing.FileName = up.FileName;
            existing.FileSize = up.FileSize;
            existing.FileType = up.FileType;
            existing.Checksum = up.Checksum;
            existing.LocalPath = up.TargetPath;
            existing.SharedSince = up.SharedSinceUtc;
            existing.IsAvailable = true;
        }

        await db.SaveChangesAsync();
    }


    public void SetDownloadTarget(Guid fileId, string path)
    {
        _downloadTargets[fileId] = path;
    }


    public bool TryGetKnownFile(Guid id, out SharedFileDto dto)
        => _knownFiles.TryGetValue(id, out dto);



    public async Task RequestFileRoutedAsync(
    Guid fileId,
    PeerNode ownerPeer,
    string ownerProtocolPeerId,
    CancellationToken ct = default)
    {
        var (sid, ctx, viaServer) = await GetRouteAsync(ownerPeer, ct);

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sid,
            w =>
            {
                if (viaServer)
                {
                    BinaryCodec.WriteString(w, "RequestFileFor");
                    BinaryCodec.WriteString(w, ownerProtocolPeerId);
                    w.Write(fileId.ToByteArray());
                }
                else
                {
                    BinaryCodec.WriteString(w, "RequestFile");
                    w.Write(fileId.ToByteArray());
                }
            });

        await Framing.WriteAsync(ctx.Stream, msg);
    }
}
