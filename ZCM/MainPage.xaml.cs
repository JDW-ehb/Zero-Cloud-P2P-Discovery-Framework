using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.ApplicationModel;
using ZCL.API;
using ZCL.Models;
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
        _timer?.Stop();
    }

    private void SyncPeers()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // add/update from store
            foreach (var p in _store.Peers)
            {
                var existing = Peers.FirstOrDefault(x => x.ProtocolPeerId == p.ProtocolPeerId);
                if (existing == null)
                {
                    Peers.Add(new PeerNodeCard(p));
                }
                else
                {
                    existing.UpdateFrom(p);
                }
            }

            // remove peers not in store anymore (usually never happens, but keeps it clean)
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
        await DisplayAlert("LLM", "Navigate to LLM page here.", "OK");
    }

    private async void ShareButton_Clicked(object sender, EventArgs e)
    {
        await DisplayAlert("Share", "Navigate to Share page here.", "OK");
    }

    // ---------------------------
    // UI model (not EF entity)
    // ---------------------------
    public sealed class PeerNodeCard : INotifyPropertyChanged
    {
        private PeerNode _peer;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProtocolPeerId => _peer.ProtocolPeerId;
        public string HostName => _peer.HostName;

        // Working rule: UP if seen in last 10 seconds
        public bool IsUp => (DateTime.UtcNow - _peer.LastSeen).TotalSeconds <= 10;

        public string StatusText => $"Status: {(IsUp ? "UP" : "Down")}";
        public string LastSeenText => $"Lastseen: {ToTimeAgo(_peer.LastSeen)}";

        public PeerNodeCard(PeerNode peer)
        {
            _peer = peer;
        }

        public void UpdateFrom(PeerNode peer)
        {
            _peer = peer;

            // Underlying data changed, notify
            OnPropertyChanged(nameof(HostName));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastSeenText));
        }

        public void RefreshComputedText()
        {
            // Computed values depend on current time, so raise changes
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastSeenText));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string ToTimeAgo(DateTime utcTime)
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
    }
}
