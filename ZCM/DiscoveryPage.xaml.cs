using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZCL.API;
using ZCL.Models;

namespace ZCM;

public partial class DiscoveryPage : ContentPage
{
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
    // + implements INotifyPropertyChanged so the UI can update without rebuilding the list.
    public sealed class PeerNodeView : INotifyPropertyChanged
    {
        public PeerNode Peer { get; }

        // Convenience properties for XAML bindings if you used old names
        public string HostName => Peer.HostName;
        public string IpAddress => Peer.IpAddress;
        public DateTime LastSeen => Peer.LastSeen;
        public string ProtocolPeerId => Peer.ProtocolPeerId;

        public string LastSeenSeconds => ToTimeAgo(Peer.LastSeen);

        public event PropertyChangedEventHandler? PropertyChanged;

        public PeerNodeView(PeerNode peer) => Peer = peer;

        public void RefreshTimeAgo()
            => OnPropertyChanged(nameof(LastSeenSeconds));

        public void RefreshAll()
        {
            OnPropertyChanged(nameof(HostName));
            OnPropertyChanged(nameof(IpAddress));
            OnPropertyChanged(nameof(LastSeen));
            OnPropertyChanged(nameof(LastSeenSeconds));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly DataStore _store;
    private IDispatcherTimer? _timer;

    // This is what your CollectionView binds to.
    public ObservableCollection<PeerNodeView> Peers { get; } = new();

    // Index by ProtocolPeerId so we can update items in-place (no flicker).
    private readonly Dictionary<string, PeerNodeView> _index = new();

    public DiscoveryPage()
    {
        InitializeComponent();

        _store = ServiceHelper.GetService<DataStore>();

        BindingContext = this;

        // Initial fill
        SyncFromStore();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Tick: update time-ago labels + sync with discovery changes.
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, __) =>
        {
            SyncFromStore();

            // Only refresh the derived "time ago" text (no list rebuild).
            foreach (var v in Peers)
                v.RefreshTimeAgo();
        };
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

    private void SyncFromStore()
    {
        // Ensure we're on UI thread (timer already is, but safe).
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Add/update
            foreach (var peer in _store.Peers.OrderByDescending(p => p.LastSeen))
            {
                var key = peer.ProtocolPeerId;

                if (_index.TryGetValue(key, out var existing))
                {
                    // PeerNode is a reference type; discovery updates fields on the same instance.
                    // We just need to tell the UI to refresh the displayed fields.
                    existing.RefreshAll();
                }
                else
                {
                    var view = new PeerNodeView(peer);
                    _index[key] = view;
                    Peers.Add(view);
                }
            }

            // Remove peers that disappeared (optional, but keeps UI clean)
            for (int i = Peers.Count - 1; i >= 0; i--)
            {
                var key = Peers[i].ProtocolPeerId;
                if (_store.Peers.All(p => p.ProtocolPeerId != key))
                {
                    _index.Remove(key);
                    Peers.RemoveAt(i);
                }
            }
        });
    }

    private void BackButton_Clicked(object sender, EventArgs e)
    {
        Navigation.PopModalAsync();
    }
}
