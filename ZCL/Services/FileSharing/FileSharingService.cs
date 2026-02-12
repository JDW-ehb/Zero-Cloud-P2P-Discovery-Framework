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

public sealed class FileSharingService : IZcspService
{
    public string ServiceName => "FileSharing";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<string> _downloadDirectoryProvider;
    private readonly ZcspPeer _peer;
    private readonly Dictionary<Guid, string> _downloadTargets = new();

    private NetworkStream? _stream;
    private Guid _currentSessionId;
    private string? _remoteProtocolPeerId;

    private Guid _remotePeerDbId;
    private Guid _localPeerDbId;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private const int ChunkSize = 64 * 1024;

    private readonly Dictionary<Guid, SharedFileDto> _knownFiles = new();
    private readonly Dictionary<Guid, FileStream> _activeDownloads = new();
    private readonly Dictionary<Guid, long> _receivedBytes = new();

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

        Debug.WriteLine("[FileSharing] Service constructed");
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
        Debug.WriteLine("[FileSharing] Stream bound");
        _stream?.Dispose();
        _stream = stream;
    }

    public async Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
    {
        Debug.WriteLine($"[FileSharing] Session started {sessionId}");

        _currentSessionId = sessionId;
        _remoteProtocolPeerId = remotePeerId;

        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var remote = await peers.GetByProtocolPeerIdAsync(remotePeerId)
            ?? throw new InvalidOperationException($"Unknown remote peer '{remotePeerId}'");

        _remotePeerDbId = remote.PeerId;

        var localId = await peers.GetLocalPeerIdAsync();
        if (localId == null)
            throw new InvalidOperationException("Local peer id not found");

        _localPeerDbId = localId.Value;

        Debug.WriteLine($"[FileSharing] Local={_localPeerDbId}, Remote={_remotePeerDbId}");
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
        Debug.WriteLine($"[FileSharing] Session closed {sessionId}");
        return CloseCurrentSessionAsync();
    }

    // =========================
    // REQUESTS (CLIENT SIDE)
    // =========================

    public async Task RequestListAsync()
    {
        if (!IsConnected)
            return;

        Debug.WriteLine("[FileSharing] Requesting file list");

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

        Debug.WriteLine($"[FileSharing] Requesting file {fileId}");

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
    // HANDLERS (SERVER SIDE)
    // =========================

    private async Task HandleListFilesAsync(Guid sessionId)
    {
        Debug.WriteLine("[FileSharing] Handling ListFiles");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == _localPeerDbId)
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

        await Framing.WriteAsync(_stream!, payload);
    }

    private async void HandleFilesResponse(BinaryReader reader)
    {
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

        var existing = db.SharedFiles
            .Where(f => f.PeerRefId == _remotePeerDbId);

        db.SharedFiles.RemoveRange(existing);

        foreach (var dto in files)
        {
            db.SharedFiles.Add(new SharedFileEntity
            {
                FileId = dto.FileId,
                PeerRefId = _remotePeerDbId,
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

    public async Task WaitForSessionBindingAsync(CancellationToken ct = default)
    {
        if (_remotePeerDbId != Guid.Empty)
            return;

        for (int i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (_remotePeerDbId != Guid.Empty)
                return;

            await Task.Delay(50, ct);
        }

        throw new TimeoutException("FileSharing session bind timeout");
    }

    public void SetDownloadTarget(Guid fileId, string path)
    {
        _downloadTargets[fileId] = path;
    }

}
