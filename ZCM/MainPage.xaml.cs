using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Repositories.Peers;
using ZCM.Pages;

namespace ZCM;

public partial class MainPage : ContentPage
{
    private readonly DataStore _store;


    public ObservableCollection<PeerNodeCard> Peers { get; } = new();

    private IDispatcherTimer? _timer;

    public MainPage()
    {
        InitializeComponent();

        _store = ServiceHelper.GetService<DataStore>();

        BindingContext = this;

        // initial sync
        SyncPeers();

        // refresh computed fields (Status/LastSeen) + add/remove peers
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, __) =>
        {
            SyncPeers();
            foreach (var card in Peers)
                card.RefreshComputedText();
        };
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
    }

    private void SyncPeers()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            using var db = ZCDPPeer.CreateDBContext(
                Path.Combine(FileSystem.AppDataDirectory, Config.Instance.DBFileName)
            );

            foreach (var p in _store.Peers.Where(p => !p.IsLocal))
            {
                var existing = Peers.FirstOrDefault(x => x.ProtocolPeerId == p.ProtocolPeerId);

                if (existing == null)
                {
                    Peers.Add(new PeerNodeCard(p, db));
                }
                else
                {
                    existing.UpdateFrom(p, db);
                }
            }

            for (int i = Peers.Count - 1; i >= 0; i--)
            {
                var card = Peers[i];
                if (!_store.Peers.Any(p => p.ProtocolPeerId == card.ProtocolPeerId))
                    Peers.RemoveAt(i);
            }
        });
    }


    // ---------------------------
    // Navigation buttons
    // ---------------------------
    private async void MessagingButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new MessagingPage());
    }

    private async void LlmButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new AiChatPage());
    }

    private async void ShareButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new ZCM.Pages.FileSharingPage());
    }

    private async void SettingsButton_Clicked(object sender, EventArgs e)
    {
        //await Navigation.PushAsync(new ZCM.Pages.SettingsPage());
    }


    private async void OnPeerTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not PeerNodeCard card)
            return;

        // modal "popup-like" page
        await Navigation.PushModalAsync(new PeerDetailsPage(card));
    }

    // ---------------------------
    // UI model (not EF entity)
    // ---------------------------
    public sealed class PeerNodeCard : INotifyPropertyChanged
    {
        private PeerNode _peer;

        public event PropertyChangedEventHandler? PropertyChanged;

        // Basic identifiers
        public Guid PeerId => _peer.PeerId;
        public string ProtocolPeerId => _peer.ProtocolPeerId;
        public string HostName => _peer.HostName;
        public string IpAddress => _peer.IpAddress;

        public DateTime FirstSeen => _peer.FirstSeen;
        public DateTime LastSeen => _peer.LastSeen;
        public PeerOnlineStatus OnlineStatus => _peer.OnlineStatus;

        // Working rule: UP if seen in last 10 seconds
        public bool IsUp => (DateTime.UtcNow - _peer.LastSeen).TotalSeconds <= 10;

        public string StatusText => $"Status: {(IsUp ? "UP" : "Down")}";
        public string LastSeenText => $"Lastseen: {ToTimeAgo(_peer.LastSeen)}";

        // ✅ Services
        public ObservableCollection<string> Services { get; } = new();
        public bool HasServices => Services.Count > 0;

        public PeerNodeCard(PeerNode peer, ServiceDBContext db)
        {
            _peer = peer;
            LoadServices(db);
        }

        private void LoadServices(ServiceDBContext db)
        {
            var services = db.Services
                .Where(s => s.PeerRefId == _peer.PeerId)
                .Select(s => s.Name == "AIChat" && s.Metadata != null
                    ? $"{s.Name} ({s.Metadata})"
                    : s.Name);


            if (Services.SequenceEqual(services))
                return;

            Services.Clear();
            foreach (var s in services)
                Services.Add(s);

            OnPropertyChanged(nameof(HasServices));
        }

        public PeerNode ToPeerNode() => _peer;


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


        // Call this from your discovery layer whenever services change
        public void UpdateServices(IEnumerable<string> services)
        {
            Services.Clear();
            foreach (var s in services.Distinct())
                Services.Add(s);

            OnPropertyChanged(nameof(HasServices));
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
