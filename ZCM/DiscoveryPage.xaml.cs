using System.Collections.ObjectModel;
using ZCL.API;
using ZCL.Models;

namespace ZCM;

public partial class DiscoveryPage : ContentPage
{
    private readonly DataStore _store;

    private IDispatcherTimer? _timer;

    public static string ToTimeAgo(DateTime utcTime)
    {
        var diff = DateTime.UtcNow - utcTime;

        if (diff.TotalSeconds < 60)
            return $"{Math.Max(1, (int)diff.TotalSeconds)}s ago";

        if (diff.TotalMinutes < 60)
            return $"{(int)Math.Round(diff.TotalMinutes)}m ago";

        if (diff.TotalHours < 24)
            return $"{(int)Math.Round(diff.TotalHours)}h ago";

        if (diff.TotalDays < 7)
            return $"{(int)Math.Round(diff.TotalDays)}d ago";

        if (diff.TotalDays < 30)
            return $"{(int)Math.Round(diff.TotalDays / 7)}w ago";

        if (diff.TotalDays < 365)
            return $"{(int)Math.Round(diff.TotalDays / 30)}mo ago";

        return $"{(int)Math.Round(diff.TotalDays / 365)}y ago";
    }

    // UI-friendly wrapper: keeps PeerNode clean (EF entity) and still gives you "LastSeenSeconds"
    public sealed class PeerNodeView
    {
        public PeerNode Peer { get; }
        public string LastSeenSeconds => ToTimeAgo(Peer.LastSeen);

        // Convenience properties for XAML bindings if you used old names
        public string HostName => Peer.HostName;
        public string IpAddress => Peer.IpAddress;
        public DateTime LastSeen => Peer.LastSeen;
        public string ProtocolPeerId => Peer.ProtocolPeerId;

        public PeerNodeView(PeerNode peer) => Peer = peer;
    }

    public ObservableCollection<PeerNodeView> Peers { get; } = new();

    public DiscoveryPage()
    {
        InitializeComponent();

        _store = ServiceHelper.GetService<DataStore>();

        BindingContext = this;

        // Initial fill
        RefreshPeers();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Refresh every second so "time ago" updates + new peers appear
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, __) => RefreshPeers();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    private void RefreshPeers()
    {
        // Run on UI thread (timer already is, but safe)
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Peers.Clear();
            foreach (var p in _store.Peers.OrderByDescending(p => p.LastSeen))
                Peers.Add(new PeerNodeView(p));
        });
    }

    private void BackButton_Clicked(object sender, EventArgs e)
    {
        Navigation.PopModalAsync();
    }
}
