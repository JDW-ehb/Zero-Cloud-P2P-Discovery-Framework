using Microsoft.Maui.Dispatching;
using ZCL.Models;
using ZCM;

namespace ZCM.Pages;

public partial class DiscoveryPopup : ContentPage
{
    private readonly MainPage _mainPage;
    private IDispatcherTimer? _timer;

    public DiscoveryPopup(MainPage main)
    {
        InitializeComponent();
        _mainPage = main;
        BindingContext = main;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Start a timer to force UI refresh
        if (_timer == null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500); // Refresh every 500ms
            _timer.Tick += (_, __) =>
            {
                // Force refresh of all peer cards
                foreach (var peer in _mainPage.Peers)
                {
                    peer.RefreshComputedText();
                }
            };
            _timer.Start();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnPeerTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not MainPage.PeerNodeCard card)
            return;

        await Navigation.PushModalAsync(
            new PeerDetailsPage { Card = card },
            false);
    }

    private async void OnMessagingPageTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not PeerNode peer)
            return;

        // close the popup/modal first, then Shell navigate
        await Navigation.PopModalAsync(false);

        await Shell.Current.GoToAsync(nameof(MessagingPage),
            new Dictionary<string, object> { { "Peer", peer } });
    }
}