using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Services.FileSharing;
using ZCL.Repositories.Peers;

namespace ZCM.ViewModels;

public sealed class FileSharingHubViewModel : BindableObject
{
    private readonly FileSharingService _service;
    private readonly IPeerRepository _peers;

    public ObservableCollection<PeerNode> Peers { get; } = new();
    public ObservableCollection<SharedFileItem> Files { get; } = new();
    public ObservableCollection<SharedFileEntity> LocalFiles { get; } = new();

    private PeerNode? _activePeer;

    public ICommand DownloadCommand { get; }
    public ICommand AddLocalFilesCommand { get; }

    public FileSharingHubViewModel(
        FileSharingService service,
        IPeerRepository peers)
    {
        _service = service;
        _peers = peers;

        _service.FilesReceived += OnFilesReceived;

        DownloadCommand = new Command<SharedFileItem>(async file =>
        {
            if (file == null) return;
            await _service.RequestFileAsync(file.FileId);
        });

        AddLocalFilesCommand = new Command(async () =>
            await PickAndShareFilesAsync());

        _ = LoadPeersAsync();
        _ = LoadLocalSharedFilesAsync();
    }

    public async Task ActivatePeerAsync(PeerNode peer)
    {
        if (_activePeer?.ProtocolPeerId == peer.ProtocolPeerId)
            return;

        _activePeer = peer;
        Files.Clear();

        await _service.EnsureSessionAsync(peer);
        await _service.WaitForSessionBindingAsync(); 
        await _service.RequestListAsync();

    }

    private async Task LoadPeersAsync()
    {
        var all = await _peers.GetAllAsync();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Peers.Clear();
            foreach (var p in all.Where(p => !p.IsLocal))
                Peers.Add(p);
        });
    }

    private void OnFilesReceived(IReadOnlyList<SharedFileDto> files)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Files.Clear();
            foreach (var f in files)
            {
                Files.Add(new SharedFileItem
                {
                    FileId = f.FileId,
                    Name = f.Name,
                    Type = f.Type,
                    Size = f.Size,
                    SharedSince = f.SharedSince
                });
            }
        });
    }

    private async Task LoadLocalSharedFilesAsync()
    {
        var localPeerId = await _peers.GetLocalPeerIdAsync();
        if (localPeerId == null)
            return;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = db.SharedFiles
            .Where(f => f.PeerRefId == localPeerId && f.IsAvailable)
            .ToList();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            LocalFiles.Clear();
            foreach (var f in files)
                LocalFiles.Add(f);
        });
    }

    private async Task PickAndShareFilesAsync()
    {
        var result = await FilePicker.PickMultipleAsync();
        if (result == null)
            return;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var localPeerId = await _peers.GetLocalPeerIdAsync();
        if (localPeerId == null)
            return;

        foreach (var file in result)
        {
            var info = new FileInfo(file.FullPath);
            db.SharedFiles.Add(new SharedFileEntity
            {
                FileId = Guid.NewGuid(),
                PeerRefId = localPeerId.Value,
                FileName = info.Name,
                FileSize = info.Length,
                FileType = info.Extension.TrimStart('.'),
                Checksum = SharedDirectoryScanner.ComputeSha256(file.FullPath),
                LocalPath = file.FullPath,
                SharedSince = DateTime.UtcNow,
                IsAvailable = true
            });
        }

        await db.SaveChangesAsync();
        await LoadLocalSharedFilesAsync();
    }

    public async Task AddFilesFromPathsAsync(IEnumerable<string> paths)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var localPeerId = await _peers.GetLocalPeerIdAsync();
        if (localPeerId == null)
            return;

        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;

            var info = new FileInfo(path);

            db.SharedFiles.Add(new SharedFileEntity
            {
                FileId = Guid.NewGuid(),
                PeerRefId = localPeerId.Value,
                FileName = info.Name,
                FileSize = info.Length,
                FileType = info.Extension.TrimStart('.'),
                Checksum = SharedDirectoryScanner.ComputeSha256(path),
                LocalPath = path,
                SharedSince = DateTime.UtcNow,
                IsAvailable = true
            });
        }

        await db.SaveChangesAsync();
        await LoadLocalSharedFilesAsync();
    }

}
