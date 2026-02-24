using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZCL.API;
using ZCL.Models;
using ZCM.Pages;

namespace ZCM;

/// <summary>
/// Main discovery dashboard.
/// Displays all discovered peers in the LAN and refreshes their status in real-time.
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly DataStore _store;

    /// <summary>
    /// UI collection bound to the CollectionView.
    /// Contains lightweight wrapper objects (PeerNodeCard) instead of EF entities.
    /// </summary>
    public ObservableCollection<PeerNodeCard> Peers { get; } = new();

    /// <summary>
    /// Periodic UI refresh timer.
    /// Updates computed properties like "Status" and "LastSeen".
    /// </summary>
    private IDispatcherTimer? _timer;

    public MainPage()
    {
        InitializeComponent();

        _store = ServiceHelper.GetService<DataStore>();
        BindingContext = this;
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
            };

            _timer.Start();
        }

        SyncPeers();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _timer?.Stop();
        _timer = null;
    }

    /// <summary>
    /// Synchronizes UI collection with in-memory DataStore.
    /// Adds new peers, updates existing ones, removes missing peers.
    /// </summary>
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

    // ---------------------------
    // Navigation Buttons
    // ---------------------------

    private async void MessagingButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(MessagingPage));

    private async void LlmButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(LLMChatPage));

    private async void ShareButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(FileSharingPage));

    private async void SettingsButton_Clicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(SettingsPage));

    private async void OnPeerTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not PeerNodeCard card)
            return;

        await Shell.Current.GoToAsync(nameof(PeerDetailsPage),
            new Dictionary<string, object>
            {
                { "Card", card }
            });
    }

    // =====================================================
    // UI Wrapper Model (NOT EF Entity)
    // =====================================================

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