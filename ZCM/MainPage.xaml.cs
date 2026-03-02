using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ZCL.API;
using ZCL.Models;
using ZCM.Pages;

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
        const int steps = 60; // smoothness

        var path = new PathF();
        float step = sweepDeg / steps;

        // Outer arc
        for (int i = 0; i <= steps; i++)
        {
            float angle = (startDeg + step * i) * MathF.PI / 180f;
            float x = cx + outerR * MathF.Cos(angle);
            float y = cy + outerR * MathF.Sin(angle);

            if (i == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }

        // Inner arc (reverse)
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
    private readonly DataStore _store;
    private readonly PeerDonutDrawable _donutDrawable = new();

    public ObservableCollection<PeerNodeCard> Peers { get; } = new();
    public ObservableCollection<ConversationPreview> RecentConversations { get; } = new();

    public int OnlineCount => Peers.Count(p => p.IsUp);
    public int OfflineCount => Peers.Count(p => !p.IsUp);
    public int SharedFilesCount => 0;

    public int MessagingPeersCount =>
        Peers.Count(p => p.Services.Any(s => s.Contains("Messaging")));

    public int FileSharingPeersCount =>
        Peers.Count(p => p.Services.Any(s => s.Contains("Filesharing")));

    public int AvailableModelsCount =>
        Peers.Sum(p => p.Services.Count(s => s.StartsWith("LLMChat")));

    public ObservableCollection<string> ActivityFeed { get; } = new()
    {
        "System started",
        "Waiting for peer activity..."
    };

    private IDispatcherTimer? _timer;

    public MainPage()
    {
        InitializeComponent();

        _store = ServiceHelper.GetService<DataStore>();
        BindingContext = this;

        // Hook drawable to GraphicsView (defined in XAML)
        PeerDonutView.Drawable = _donutDrawable;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (_timer == null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);

            _timer.Tick += (_, __) =>
            {
                SyncPeers();

                foreach (var card in Peers)
                    card.RefreshComputedText();

                RefreshDashboardCounts();
            };

            _timer.Start();
        }

        SyncPeers();
        SyncConversations();
        RefreshDashboardCounts();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private void RefreshDashboardCounts()
    {
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(SharedFilesCount));
        OnPropertyChanged(nameof(MessagingPeersCount));
        OnPropertyChanged(nameof(FileSharingPeersCount));
        OnPropertyChanged(nameof(AvailableModelsCount));

        // Update donut values
        _donutDrawable.Online = OnlineCount;
        _donutDrawable.Offline = OfflineCount;

        PeerDonutView.Invalidate();
    }

    private async void DiscoveryButton_Clicked(object sender, EventArgs e)
        => await Navigation.PushModalAsync(new DiscoveryPopup(this), false);

    private async void MessagingButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(MessagingPage));

    private async void LlmButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(LLMChatPage));

    private async void ShareButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(FileSharingPage));

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

        foreach (var p in _store.Peers.Where(p => !p.IsLocal))
        {
            var existing = Peers.FirstOrDefault(x => x.ProtocolPeerId == p.ProtocolPeerId);

            if (existing == null)
                Peers.Add(new PeerNodeCard(p, db));
            else
                existing.UpdateFrom(p, db);
        }

        for (int i = Peers.Count - 1; i >= 0; i--)
        {
            var card = Peers[i];

            if (!_store.Peers.Any(p => p.ProtocolPeerId == card.ProtocolPeerId))
                Peers.RemoveAt(i);
        }
    }

    public class ConversationPreview
    {
        public string PeerName { get; set; } = "";
        public string LastMessage { get; set; } = "";
        public bool IsOnline { get; set; }
        public string LastSeenText { get; set; } = "";
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
            LoadServices(db);

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
}