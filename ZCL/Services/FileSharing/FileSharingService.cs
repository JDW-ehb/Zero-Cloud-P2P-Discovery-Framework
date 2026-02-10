using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net.Sockets;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;


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
    private NetworkStream? _stream;

    private const int ChunkSize = 64 * 1024; // 64 KB

    public event Action<IReadOnlyList<SharedFileDto>>? FilesReceived;
    public event Action<Guid, double>? TransferProgress;
    public event Action<Guid, string>? TransferCompleted;

    private readonly Dictionary<Guid, FileStream> _activeDownloads = new();
    private readonly Dictionary<Guid, long> _receivedBytes = new();

    private Guid _currentSessionId;
    private string? _remotePeerId;

    public Guid CurrentSessionId => _currentSessionId;
    public bool IsConnected => _stream != null && _currentSessionId != Guid.Empty;

    private readonly Func<string> _downloadDirectoryProvider;

    public FileSharingService(
        IServiceScopeFactory scopeFactory,
        Func<string> downloadDirectoryProvider)
    {
        _scopeFactory = scopeFactory;
        _downloadDirectoryProvider = downloadDirectoryProvider;
    }


    public void BindStream(NetworkStream stream)
    {
        _stream?.Dispose(); 
        _stream = stream;
    }


    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
    {
        _currentSessionId = sessionId;
        _remotePeerId = remotePeerId;
        return Task.CompletedTask;
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
    // Handlers
    // =========================

    private async Task HandleListFilesAsync(Guid sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var transferPeer = await db.FileTransfers
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.PeerRefId)
            .FirstOrDefaultAsync();

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == transferPeer && f.IsAvailable)
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


    private async Task HandleFileRequestAsync(Guid sessionId, Guid fileId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var file = await db.SharedFiles.FirstOrDefaultAsync(f => f.FileId == fileId);
        if (file == null || !File.Exists(file.LocalPath))
            return;

        var transfer = new FileTransferEntity
        {
            TransferId = Guid.NewGuid(),
            FileId = file.FileId,
            PeerRefId = file.PeerRefId,
            SessionId = sessionId,
            FileName = file.FileName,
            FileSize = file.FileSize,
            Checksum = file.Checksum,
            Status = FileTransferStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };

        db.FileTransfers.Add(transfer);
        await db.SaveChangesAsync();

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
                    w.Write(transfer.TransferId.ToByteArray());
                    w.Write(read);
                    w.Write(buffer, 0, read);
                });

            await Framing.WriteAsync(_stream!, chunk);
        }

        transfer.Status = FileTransferStatus.Completed;
        transfer.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var done = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "FileComplete");
                w.Write(transfer.TransferId.ToByteArray());
                BinaryCodec.WriteString(w, transfer.Checksum);
            });

        await Framing.WriteAsync(_stream!, done);
    }
    private void HandleFilesResponse(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var files = new List<SharedFileDto>(count);

        for (int i = 0; i < count; i++)
        {
            var id = new Guid(reader.ReadBytes(16));
            var name = BinaryCodec.ReadString(reader);
            var type = BinaryCodec.ReadString(reader);
            var size = reader.ReadInt64();
            var checksum = BinaryCodec.ReadString(reader);
            var since = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);

            files.Add(new SharedFileDto(id, name, type, size, checksum, since));
        }

        FilesReceived?.Invoke(files);
    }
    private void HandleFileChunk(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        int length = reader.ReadInt32();
        var data = reader.ReadBytes(length);

        if (!_activeDownloads.TryGetValue(transferId, out var fs))
        {
            var baseDir = _downloadDirectoryProvider();
            Directory.CreateDirectory(baseDir);

            var path = Path.Combine(baseDir, transferId.ToString());

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            fs = File.Create(path);

            _activeDownloads[transferId] = fs;
            _receivedBytes[transferId] = 0;
        }

        fs.Write(data, 0, data.Length);
        _receivedBytes[transferId] += data.Length;

        TransferProgress?.Invoke(transferId, _receivedBytes[transferId]);
    }

    private void HandleFileComplete(BinaryReader reader)
    {
        var transferId = new Guid(reader.ReadBytes(16));
        var checksum = BinaryCodec.ReadString(reader);

        if (_activeDownloads.TryGetValue(transferId, out var fs))
        {
            fs.Dispose();
            _activeDownloads.Remove(transferId);
            _receivedBytes.Remove(transferId);
        }

        TransferCompleted?.Invoke(transferId, checksum);
    }
    public async Task RequestListAsync(Guid sessionId)
    {
        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w => BinaryCodec.WriteString(w, "ListFiles"));

        await Framing.WriteAsync(_stream!, msg);
    }

    public async Task RequestFileAsync(Guid sessionId, Guid fileId)
    {
        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "RequestFile");
                w.Write(fileId.ToByteArray());
            });

        await Framing.WriteAsync(_stream!, msg);
    }
    public Task CloseCurrentSessionAsync()
    {
        try
        {
            _stream?.Close();
            _stream?.Dispose();
        }
        catch
        {
        }

        _stream = null;
        _currentSessionId = Guid.Empty;
        _remotePeerId = null;

        // Abort any in-progress downloads
        foreach (var fs in _activeDownloads.Values)
        {
            try { fs.Dispose(); } catch { }
        }

        _activeDownloads.Clear();
        _receivedBytes.Clear();

        return Task.CompletedTask;
    }


}
