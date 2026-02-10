using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Services.FileSharing;

namespace ZCM.ViewModels;

public sealed class FileSharingViewModel : BindableObject
{
    public ObservableCollection<SharedFileItem> Files { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand DownloadCommand { get; }

    private readonly FileSharingService _service;
    private readonly Guid _sessionId;

    public FileSharingViewModel(FileSharingService service, Guid sessionId)
    {
        _service = service;
        _sessionId = sessionId;

        _service.FilesReceived += OnFilesReceived;

        RefreshCommand = new Command(async () =>
            await _service.RequestListAsync(_sessionId));

        DownloadCommand = new Command<SharedFileItem>(async file =>
        {
            if (file == null) return;
            await _service.RequestFileAsync(_sessionId, file.FileId);
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
}
