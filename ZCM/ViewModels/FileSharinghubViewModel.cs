using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using ZCL.Models;
using ZCL.Services.FileSharing;
using ZCL.Repositories.Peers;
#if WINDOWS
using Windows.Storage.Pickers;
using WinRT.Interop;
#endif



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
    public ICommand RemoveLocalFileCommand { get; }

    private bool _isDownloading;
    private double _downloadProgress;
    private string _downloadFileName = string.Empty;

    // ── Aggregate-view state ──
    private enum FileView { None, MyFiles, AllNetwork, Peer }
    private FileView _currentView = FileView.None;
    private readonly ConcurrentDictionary<Guid, PeerNode> _fileOwnerMap = new();

    private string _filesHeaderText = "Shared files";
    public string FilesHeaderText
    {
        get => _filesHeaderText;
        set { _filesHeaderText = value; OnPropertyChanged(); }
    }

    public FileSharingHubViewModel(
        FileSharingService service,
        IPeerRepository peers)
    {
        _service = service;
        _peers = peers;

        _service.FilesReceived += OnFilesReceived;
        _service.TransferProgress += OnTransferProgress;
        _service.TransferCompleted += OnTransferCompleted;


        DownloadCommand = new Command<SharedFileItem>(async file =>
        {
            if (file == null)
                return;

#if WINDOWS
    var picker = new FileSavePicker();

    picker.SuggestedFileName = file.Name;

    picker.FileTypeChoices.Add(
        file.Type.ToUpper(),
        new List<string> { $".{file.Type}" });

    var window = Application.Current?.Windows[0];
    var hwnd = WindowNative.GetWindowHandle(window.Handler.PlatformView);
    InitializeWithWindow.Initialize(picker, hwnd);

    var result = await picker.PickSaveFileAsync();

    if (result == null)
        return;

    // Local file → just copy it directly, no network needed
    if (file.IsLocal)
    {
        var localPath = await ResolveLocalPathAsync(file.FileId);
        if (localPath != null && File.Exists(localPath))
        {
            File.Copy(localPath, result.Path, overwrite: true);
            return;
        }
    }

    _service.SetDownloadTarget(file.FileId, result.Path);
#endif

            // Resolve the owner peer: use _activePeer when viewing
            // a single peer, otherwise look up from the file owner map.
            var owner = _activePeer;
            if (owner == null)
                _fileOwnerMap.TryGetValue(file.FileId, out owner);

            if (owner == null)
                return;

            await _service.RequestFileRoutedAsync(
                file.FileId,
                ownerPeer: owner,
                ownerProtocolPeerId: owner.ProtocolPeerId);
        });



        AddLocalFilesCommand = new Command(async () =>
            await PickAndShareFilesAsync());

        RemoveLocalFileCommand = new Command<SharedFileEntity>(async file =>
        {
            if (file == null)
                return;

            await RemoveLocalFileAsync(file);
        });

        _ = LoadPeersAsync();
        _ = LoadLocalSharedFilesAsync();
    }

    /// <summary>
    /// Look up the local disk path for a file owned by this peer.
    /// </summary>
    private async Task<string?> ResolveLocalPathAsync(Guid remoteFileId)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var entity = await db.SharedFiles
            .FirstOrDefaultAsync(f => f.RemoteFileId == remoteFileId);

        return entity?.LocalPath;
    }


    public async Task ActivatePeerAsync(PeerNode peer)
    {
        if (_activePeer?.ProtocolPeerId == peer.ProtocolPeerId
            && _currentView == FileView.Peer)
            return;

        _activePeer = peer;
        _currentView = FileView.Peer;
        _fileOwnerMap.Clear();
        FilesHeaderText = $"{peer.HostName}'s files";

        await LoadRemoteFilesFromDbAsync(peer);

        if (peer.OnlineStatus != PeerOnlineStatus.Online)
            return;

        try
        {
            await _service.RequestListRoutedAsync(
                ownerPeer: peer,
                targetProtocolPeerId: peer.ProtocolPeerId);
        }
        catch (SocketException)
        {
            Debug.WriteLine("[FileSharingHub] Peer unreachable.");
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("[FileSharingHub] Session bind timeout.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileSharingHub] Unexpected error: {ex}");
        }
    }


    // ═══════════════════════════════════════════════════
    //  Aggregate views
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Show all files that the local peer is sharing.
    /// </summary>
    public async Task LoadMyFilesAsync()
    {
        _activePeer = null;
        _currentView = FileView.MyFiles;
        _fileOwnerMap.Clear();
        FilesHeaderText = "My Shared Files";

        var localPeerId = await _peers.GetLocalPeerIdAsync();
        if (localPeerId == null)
            return;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == localPeerId)
            .OrderByDescending(f => f.SharedSince)
            .ToListAsync();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Files.Clear();
            foreach (var f in files)
            {
                Files.Add(new SharedFileItem
                {
                    FileId = f.RemoteFileId,
                    Name = f.FileName,
                    Type = f.FileType,
                    Size = f.FileSize,
                    SharedSince = f.SharedSince,
                    IsLocal = true
                });
            }
        });
    }

    /// <summary>
    /// Show files shared by every peer in the network, including yourself.
    /// </summary>
    public async Task LoadAllNetworkFilesAsync()
    {
        _activePeer = null;
        _currentView = FileView.AllNetwork;
        _fileOwnerMap.Clear();
        FilesHeaderText = "All Network Files";

        var localPeerId = await _peers.GetLocalPeerIdAsync();
        if (localPeerId == null)
            return;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = await db.SharedFiles
            .Include(f => f.Peer)
            .OrderByDescending(f => f.SharedSince)
            .ToListAsync();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Files.Clear();
            foreach (var f in files)
            {
                var isLocal = f.PeerRefId == localPeerId;

                // Cache the owner so downloads can resolve the right peer
                if (!isLocal)
                    _fileOwnerMap[f.RemoteFileId] = f.Peer;

                Files.Add(new SharedFileItem
                {
                    FileId = f.RemoteFileId,
                    Name = f.FileName,
                    Type = f.FileType,
                    Size = f.FileSize,
                    SharedSince = f.SharedSince,
                    IsLocal = isLocal,
                    OwnerHostName = f.Peer.HostName
                });
            }
        });
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
        // Only apply live updates when viewing a single peer
        if (_currentView != FileView.Peer)
            return;

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

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == localPeerId)
            .ToListAsync();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            LocalFiles.Clear();
            foreach (var f in files)
                LocalFiles.Add(f);
        });
    }

    private async Task RemoveLocalFileAsync(SharedFileEntity file)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var entity = await db.SharedFiles
            .FirstOrDefaultAsync(f => f.RemoteFileId == file.RemoteFileId);

        if (entity == null)
            return;

        db.SharedFiles.Remove(entity);

        await db.SaveChangesAsync();
        await LoadLocalSharedFilesAsync();
    }


    private async Task PickAndShareFilesAsync()
    {
        var result = await FilePicker.PickMultipleAsync();
        if (result == null)
            return;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var localPeerId = await _peers.GetLocalPeerIdAsync();
        var localProtocolId = await _peers.GetLocalProtocolPeerIdAsync();

        if (localPeerId == null || localProtocolId == null)
            return;

        foreach (var file in result)
        {
            var info = new FileInfo(file.FullPath);

            var entity = new SharedFileEntity
            {
                Id = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
                PeerRefId = localPeerId.Value,
                FileName = info.Name,
                FileSize = info.Length,
                FileType = info.Extension.TrimStart('.'),
                Checksum = SharedDirectoryScanner.ComputeSha256(file.FullPath),
                LocalPath = file.FullPath,
                SharedSince = DateTime.UtcNow,
                IsAvailable = true
            };

            db.SharedFiles.Add(entity);

            await _service.MirrorUploadToServerAsync(
                fileId: entity.RemoteFileId,
                ownerProtocolPeerId: localProtocolId,
                fileName: entity.FileName,
                fileType: entity.FileType,
                fileSize: entity.FileSize,
                checksum: entity.Checksum,
                sharedSinceUtc: entity.SharedSince,
                localPath: entity.LocalPath);
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
                Id = Guid.NewGuid(),
                RemoteFileId = Guid.NewGuid(),
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

    private async Task LoadRemoteFilesFromDbAsync(PeerNode peer)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = await db.SharedFiles
            .Where(f => f.PeerRefId == peer.PeerId)
            .OrderByDescending(f => f.SharedSince)
            .ToListAsync();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Files.Clear();

            foreach (var f in files)
            {
                Files.Add(new SharedFileItem
                {
                    FileId = f.RemoteFileId,
                    Name = f.FileName,
                    Type = f.FileType,
                    Size = f.FileSize,
                    SharedSince = f.SharedSince
                });
            }
        });
    }


    public bool IsDownloading
    {
        get => _isDownloading;
        set { _isDownloading = value; OnPropertyChanged(); }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        set { _downloadProgress = value; OnPropertyChanged(); }
    }

    public string DownloadFileName
    {
        get => _downloadFileName;
        set { _downloadFileName = value; OnPropertyChanged(); }
    }


    private void OnTransferProgress(Guid fileId, double bytesReceived)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_service.TryGetKnownFile(fileId, out var meta))
                return;

            DownloadFileName = meta.Name;
            IsDownloading = true;

            var percent = bytesReceived / meta.Size;
            DownloadProgress = percent;
        });
    }

    private void OnTransferCompleted(Guid fileId, string checksum)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DownloadProgress = 1;
            IsDownloading = false;
        });
    }
}



