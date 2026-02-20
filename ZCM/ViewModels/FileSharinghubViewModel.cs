using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using System;
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

    _service.SetDownloadTarget(file.FileId, result.Path);
#endif

            await _service.RequestFileAsync(file.FileId);
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

    // =========================
    // PEER SELECTION
    // =========================

    public async Task ActivatePeerAsync(PeerNode peer)
    {
        if (_activePeer?.ProtocolPeerId == peer.ProtocolPeerId)
            return;

        _activePeer = peer;

        // Always load last known files
        await LoadRemoteFilesFromDbAsync(peer);

        // If offline → stop here
        if (peer.OnlineStatus != PeerOnlineStatus.Online)
            return;

        try
        {
            await _service.EnsureSessionAsync(peer.ProtocolPeerId);
            await _service.RequestListAsync();
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



    // =========================
    // PEERS
    // =========================

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

    // =========================
    // REMOTE FILES
    // =========================

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

    // =========================
    // LOCAL SHARED FILES
    // =========================

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
            .FirstOrDefaultAsync(f => f.FileId == file.FileId);

        if (entity == null)
            return;

        db.SharedFiles.Remove(entity);

        await db.SaveChangesAsync();
        await LoadLocalSharedFilesAsync();
    }

    // =========================
    // ADD FILES (PICKER)
    // =========================

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

    // =========================
    // ADD FILES (DRAG & DROP)
    // =========================

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
                    FileId = f.FileId,
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
