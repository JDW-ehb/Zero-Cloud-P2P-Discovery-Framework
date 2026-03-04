using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ZCL.API;
using ZCL.Models;
using ZCL.Repositories.Security;   // ITrustGroupRepository
using ZCL.Services.FileSharing;
using ZCM.Pages;
using ZCM.Services;

namespace ZCM;

public class PeerDonutDrawable : IDrawable
{
    public int Online { get; set; }
    public int Offline { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        int total = Online + Offline;
        if (total <= 0)
            return;

        float size = Math.Min(rect.Width, rect.Height);
        float cx = rect.Center.X;
        float cy = rect.Center.Y;

        float outerR = size / 2f;
        float innerR = outerR - 18f;

        float startAngle = -90f;
        float onlineSweep = (float)Online / total * 360f;
        float offlineSweep = 360f - onlineSweep;

        DrawSegment(canvas, cx, cy, outerR, innerR, startAngle, onlineSweep, Colors.LimeGreen);
        DrawSegment(canvas, cx, cy, outerR, innerR, startAngle + onlineSweep, offlineSweep, Colors.DarkRed);
    }

    private void DrawSegment(
        ICanvas canvas,
        float cx, float cy,
        float outerR, float innerR,
        float startDeg, float sweepDeg,
        Color color)
    {
        const int steps = 60;

        var path = new PathF();
        float step = sweepDeg / steps;

        // outer arc
        for (int i = 0; i <= steps; i++)
        {
            float angle = (startDeg + step * i) * MathF.PI / 180f;
            float x = cx + outerR * MathF.Cos(angle);
            float y = cy + outerR * MathF.Sin(angle);

            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }

        // inner arc (reverse)
        for (int i = steps; i >= 0; i--)
        {
            float angle = (startDeg + step * i) * MathF.PI / 180f;
            float x = cx + innerR * MathF.Cos(angle);
            float y = cy + innerR * MathF.Sin(angle);

            path.LineTo(x, y);
        }

        path.Close();

        canvas.FillColor = color;
        canvas.FillPath(path);
    }
}

public partial class MainPage : ContentPage
{
    private DateTime _lastListRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan ListRefreshInterval = TimeSpan.FromSeconds(10);

    public bool AdvertisesMessaging { get; private set; }
    public bool AdvertisesFileSharing { get; private set; }
    public bool AdvertisesLLMChat { get; private set; }

    public int TotalMessagesCount { get; private set; }
    public int PeersMessagedCount { get; private set; }
    public int PeersNeverMessagedCount { get; private set; }

    private readonly DataStore _store;
    private readonly ActivityService _activity;
    private readonly PeerDonutDrawable _donutDrawable = new();

    // Track previous counts to detect real changes
    private int _prevMessageCount;
    private readonly HashSet<string> _knownPeerIds = new();
    private readonly HashSet<string> _knownFileChecksums = new();

    public ObservableCollection<string> AdvertisedServices { get; } = new();
    public ObservableCollection<PeerNodeCard> Peers { get; } = new();
    public ObservableCollection<ConversationPreview> RecentConversations { get; } = new();
    public ObservableCollection<SharedFileCard> SharedFiles { get; } = new();

    public ObservableCollection<TrustGroupItem> TrustGroupLines { get; } = new();
    public int TrustedGroupsCount => TrustGroupLines.Count(g => g.IsEnabled);

    public int OnlineCount => Peers.Count(p => p.IsUp);
    public int OfflineCount => Peers.Count(p => !p.IsUp);
    public int SharedFilesCount => SharedFiles.Count;

    public int MessagingPeersCount =>
        Peers.Count(p => p.Services.Any(s => s.Contains("Messaging")));

    public int FileSharingPeersCount =>
        Peers.Count(p => p.Services.Any(s => s.Contains("Filesharing")));

    public int AvailableModelsCount =>
        Peers.Sum(p => p.Services.Count(s => s.StartsWith("LLMChat")));

    public ObservableCollection<string> ActivityFeed { get; } = new();

    private IDispatcherTimer? _timer;

    public MainPage()
    {
        InitializeComponent();

        _store = ServiceHelper.GetService<DataStore>();
        _activity = ServiceHelper.GetService<ActivityService>();
        BindingContext = this;

        PeerDonutView.Drawable = _donutDrawable;

        // Subscribe to activity events from anywhere in the app
        _activity.EntryAdded += OnActivityEntryAdded;

        // Load any entries already logged before this page was created
        foreach (var entry in _activity.GetAll())
            ActivityFeed.Add(entry);

        _activity.Log("Dashboard opened");
    }

    private void OnActivityEntryAdded(string entry)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ActivityFeed.Insert(0, entry);

            // Keep the UI list bounded
            while (ActivityFeed.Count > 50)
                ActivityFeed.RemoveAt(ActivityFeed.Count - 1);
        });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (_timer == null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);

            _timer.Tick += async (_, __) =>
            {
                SyncPeers();

                await RefreshRemoteSharedFilesAsync();
                SyncSharedFiles();

                foreach (var card in Peers)
                    card.RefreshComputedText();

                RefreshDashboardCounts();
                RefreshMessagingStats();
            };

            _timer.Start();
        }

        SyncPeers();
        SyncConversations();
        SyncSharedFiles();
        RefreshDashboardCounts();
        RefreshMessagingStats();
        _ = SyncTrustGroupsAsync();
        _ = SyncAdvertisedServicesAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private void RefreshMessagingStats()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        // total messages
        TotalMessagesCount = db.Messages.Count();

        // Log new messages
        if (TotalMessagesCount > _prevMessageCount && _prevMessageCount > 0)
        {
            var newCount = TotalMessagesCount - _prevMessageCount;
            _activity.Log($"{newCount} new message{(newCount > 1 ? "s" : "")} received");
        }
        _prevMessageCount = TotalMessagesCount;

        // peers we ever messaged
        var peersMessaged = db.Messages
            .Select(m => m.ToPeerId)
            .Distinct()
            .ToHashSet();

        PeersMessagedCount = peersMessaged.Count;

        // peers never messaged (exclude servers)
        var knownPeers = db.PeerNodes
            .Where(p => !p.IsLocal && p.Role != NodeRole.Server)
            .Select(p => p.PeerId)
            .ToList();

        PeersNeverMessagedCount = knownPeers.Count(p => !peersMessaged.Contains(p));

        OnPropertyChanged(nameof(TotalMessagesCount));
        OnPropertyChanged(nameof(PeersMessagedCount));
        OnPropertyChanged(nameof(PeersNeverMessagedCount));
    }

    private void RefreshDashboardCounts()
    {
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(SharedFilesCount));
        OnPropertyChanged(nameof(MessagingPeersCount));
        OnPropertyChanged(nameof(FileSharingPeersCount));
        OnPropertyChanged(nameof(AvailableModelsCount));

        _donutDrawable.Online = OnlineCount;
        _donutDrawable.Offline = OfflineCount;
        PeerDonutView.Invalidate();
    }

    // ✅ Trust groups -> show all with enabled/disabled status
    private async Task SyncTrustGroupsAsync()
    {
        try
        {
            using var scope = ServiceHelper.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITrustGroupRepository>();

            var all = await repo.GetAllAsync();

            var items = all
                .Select(g => new TrustGroupItem { Name = g.Name, IsEnabled = g.IsEnabled })
                .ToList();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // Only redraw if changed (prevents flicker)
                if (TrustGroupLines.Count == items.Count &&
                    TrustGroupLines.Zip(items).All(p => p.First.Name == p.Second.Name && p.First.IsEnabled == p.Second.IsEnabled))
                    return;

                TrustGroupLines.Clear();
                foreach (var item in items)
                    TrustGroupLines.Add(item);

                OnPropertyChanged(nameof(TrustedGroupsCount));
            });

            _activity.Log($"Trust groups synced — {items.Count(i => i.IsEnabled)} enabled");
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                TrustGroupLines.Clear();
                TrustGroupLines.Add(new TrustGroupItem { Name = $"Trust groups unavailable: {ex.Message}", IsEnabled = false });
                OnPropertyChanged(nameof(TrustedGroupsCount));
            });
        }
    }

    private void SyncSharedFiles()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var files = db.SharedFiles
            .Include(f => f.Peer)
            .Where(f => f.IsAvailable)
            .OrderByDescending(f => f.SharedSince)
            .ToList();

        var newKeys = files.Select(f => f.Checksum).ToHashSet();

        // Remove files that no longer exist
        for (int i = SharedFiles.Count - 1; i >= 0; i--)
        {
            if (!newKeys.Contains(SharedFiles[i].Checksum))
            {
                _activity.Log($"File removed: {SharedFiles[i].FileName}");
                _knownFileChecksums.Remove(SharedFiles[i].Checksum);
                SharedFiles.RemoveAt(i);
            }
        }

        // Add or update files
        foreach (var file in files)
        {
            var existing = SharedFiles.FirstOrDefault(f => f.Checksum == file.Checksum);
            if (existing == null)
            {
                SharedFiles.Add(new SharedFileCard
                {
                    Id = file.Id,
                    FileName = file.FileName,
                    FileSize = file.FileSize,
                    FileType = file.FileType,
                    PeerName = file.Peer.HostName,
                    SharedSince = file.SharedSince,
                    Checksum = file.Checksum
                });

                if (_knownFileChecksums.Add(file.Checksum))
                    _activity.Log($"New shared file: {file.FileName} from {file.Peer.HostName}");
            }
            else
            {
                existing.Id = file.Id;
                existing.FileName = file.FileName;
                existing.FileSize = file.FileSize;
                existing.FileType = file.FileType;
                existing.PeerName = file.Peer.HostName;
                existing.SharedSince = file.SharedSince;
            }
        }
    }

    private async void DiscoveryButton_Clicked(object sender, EventArgs e)
        => await Navigation.PushModalAsync(new DiscoveryPopup(this), false);

    private async void MessagingButton_Clicked(object sender, EventArgs e)
    {
        _activity.Log("Navigated to Messaging");
        await Shell.Current.GoToAsync(nameof(MessagingPage));
    }

    private async void LlmButton_Clicked(object sender, EventArgs e)
    {
        _activity.Log("Navigated to LLM Chat");
        await Shell.Current.GoToAsync(nameof(LLMChatPage));
    }

    private async void ShareButton_Clicked(object sender, EventArgs e)
    {
        _activity.Log("Navigated to File Sharing");
        await Shell.Current.GoToAsync(nameof(FileSharingPage));
    }

    private async void SettingsButton_Clicked(object sender, EventArgs e)
        => await Navigation.PushModalAsync(new SettingsPage(), false);

    private async void OnPeerTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not PeerNodeCard card)
            return;

        var popup = new PeerDetailsPage { Card = card };
        await Navigation.PushModalAsync(popup, false);
    }

    private void SyncConversations()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var lastMessages = db.Messages
            .GroupBy(m => m.ToPeerId)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .Take(5)
            .ToList();

        RecentConversations.Clear();

        foreach (var msg in lastMessages)
        {
            var peer = Peers.FirstOrDefault(p => p.PeerId == msg.ToPeerId);
            if (peer == null)
                continue;

            RecentConversations.Add(new ConversationPreview
            {
                PeerName = peer.HostName,
                LastMessage = msg.Content,
                IsOnline = peer.IsUp,
                LastSeenText = peer.LastSeenText
            });
        }
    }

    private void SyncPeers()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        foreach (var p in _store.Peers.Where(p => !p.IsLocal && p.Role != NodeRole.Server))
        {
            var existing = Peers.FirstOrDefault(x => x.ProtocolPeerId == p.ProtocolPeerId);

            if (existing == null)
                Peers.Add(new PeerNodeCard(p, db));
            else
                existing.UpdateFrom(p, db);  // ✅ This updates LastSeen from DataStore
        }

        for (int i = Peers.Count - 1; i >= 0; i--)
        {
            var card = Peers[i];
            if (!_store.Peers.Any(p => p.ProtocolPeerId == card.ProtocolPeerId && p.Role != NodeRole.Server))
                Peers.RemoveAt(i);
        }
    }

    private async Task SyncAdvertisedServicesAsync()
    {
        try
        {
            using var scope = ServiceHelper.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

            var enabled = await db.AnnouncedServiceSettings
                .Where(x => x.IsEnabled)
                .Select(x => x.ServiceName)
                .ToListAsync();

            AdvertisesMessaging = enabled.Contains("Messaging");
            AdvertisesFileSharing = enabled.Contains("FileSharing");
            AdvertisesLLMChat = enabled.Contains("LLMChat");

            OnPropertyChanged(nameof(AdvertisesMessaging));
            OnPropertyChanged(nameof(AdvertisesFileSharing));
            OnPropertyChanged(nameof(AdvertisesLLMChat));

            _activity.Log($"Services advertised: {string.Join(", ", enabled)}");
        }
        catch
        {
            // ignore if table not ready yet
        }
    }

    private async Task RefreshRemoteSharedFilesAsync()
    {
        // throttle network calls
        if (DateTime.UtcNow - _lastListRefreshUtc < ListRefreshInterval)
            return;

        _lastListRefreshUtc = DateTime.UtcNow;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
        var fileSharing = scope.ServiceProvider.GetRequiredService<FileSharingService>();

        // Ask every online remote peer for its list (exclude servers)
        var onlinePeers = await db.PeerNodes
            .Where(p => !p.IsLocal
                     && p.Role != NodeRole.Server
                     && p.OnlineStatus == PeerOnlineStatus.Online)
            .ToListAsync();

        foreach (var peer in onlinePeers)
        {
            try
            {
                await fileSharing.RequestListRoutedAsync(
                    ownerPeer: peer,
                    targetProtocolPeerId: peer.ProtocolPeerId);
            }
            catch
            {
                // ignore per-peer failures
            }
        }
    }

    public class TrustGroupItem
    {
        public string Name { get; set; } = "";
        public bool IsEnabled { get; set; }
    }

    public class ConversationPreview
    {
        public string PeerName { get; set; } = "";
        public string LastMessage { get; set; } = "";
        public bool IsOnline { get; set; }
        public string LastSeenText { get; set; } = "";
    }

    public sealed class SharedFileCard : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _fileName = "";
        private long _fileSize;
        private string _fileType = "";
        private string _peerName = "";
        private DateTime _sharedSince;

        public Guid Id { get; set; }

        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    OnPropertyChanged();
                }
            }
        }

        public long FileSize
        {
            get => _fileSize;
            set
            {
                if (_fileSize != value)
                {
                    _fileSize = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FileSizeText));
                }
            }
        }

        public string FileType
        {
            get => _fileType;
            set
            {
                if (_fileType != value)
                {
                    _fileType = value;
                    OnPropertyChanged();
                }
            }
        }

        public string PeerName
        {
            get => _peerName;
            set
            {
                if (_peerName != value)
                {
                    _peerName = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime SharedSince
        {
            get => _sharedSince;
            set
            {
                if (_sharedSince != value)
                {
                    _sharedSince = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Checksum { get; set; } = "";

        public string FileSizeText => FormatFileSize(FileSize);

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }

    public sealed class PeerNodeCard : INotifyPropertyChanged
    {
        private PeerNode _peer;

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid PeerId => _peer.PeerId;
        public string ProtocolPeerId => _peer.ProtocolPeerId;
        public string HostName => _peer.HostName;
        public string IpAddress => _peer.IpAddress;

        public DateTime FirstSeen => _peer.FirstSeen;
        public DateTime LastSeen => _peer.LastSeen;
        public PeerOnlineStatus OnlineStatus => _peer.OnlineStatus;

        public bool IsUp => (DateTime.UtcNow - _peer.LastSeen).TotalSeconds <= 10;

        public string StatusText => $"Status: {(IsUp ? "UP" : "Down")}";
        public string LastSeenText => $"Lastseen: {ToTimeAgo(_peer.LastSeen)}";

        public ObservableCollection<string> Services { get; } = new();
        public bool HasServices => Services.Count > 0;

        public PeerNodeCard(PeerNode peer, ServiceDBContext db)
        {
            _peer = peer;
            LoadServices(db);
        }

        public PeerNode ToPeerNode() => _peer;

        private void LoadServices(ServiceDBContext db)
        {
            var services = db.Services
                .Where(s => s.PeerRefId == _peer.PeerId)
                .Select(s => s.Name == "LLMChat" && !string.IsNullOrWhiteSpace(s.Metadata)
                    ? $"{s.Name} ({s.Metadata})"
                    : s.Name)
                .ToList();

            if (Services.SequenceEqual(services))
                return;

            Services.Clear();
            foreach (var s in services)
                Services.Add(s);

            OnPropertyChanged(nameof(HasServices));
        }

        public void UpdateFrom(PeerNode peer, ServiceDBContext db)
        {
            _peer = peer;
            LoadServices(db);  // ✅ This should reload services from DB

            OnPropertyChanged(nameof(HostName));
            OnPropertyChanged(nameof(IpAddress));
            OnPropertyChanged(nameof(FirstSeen));
            OnPropertyChanged(nameof(LastSeen));
            OnPropertyChanged(nameof(OnlineStatus));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastSeenText));
        }

        public void RefreshComputedText()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastSeenText));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string ToTimeAgo(DateTime utcTime)
        {
            var diff = DateTime.UtcNow - utcTime;

            if (diff.TotalSeconds < 60) return $"{Math.Max(1, (int)diff.TotalSeconds)}s ago";
            if (diff.TotalMinutes < 60) return $"{(int)Math.Round(diff.TotalMinutes)}m ago";
            if (diff.TotalHours < 24) return $"{(int)Math.Round(diff.TotalHours)}h ago";
            if (diff.TotalDays < 7) return $"{(int)Math.Round(diff.TotalDays)}d ago";
            if (diff.TotalDays < 30) return $"{(int)Math.Round(diff.TotalDays / 7)}w ago";
            if (diff.TotalDays < 365) return $"{(int)Math.Round(diff.TotalDays / 30)}mo ago";
            return $"{(int)Math.Round(diff.TotalDays / 365)}y ago";
        }
    }

    private void ExitButton_Clicked(object sender, EventArgs e)
    {
        Application.Current?.Quit();
    }
}