using ZCL.Models;

namespace ZCM.Pages;

public partial class PeerDetailsPage : ContentPage
{
    private MainPage.PeerNodeCard? _card;

    public PeerDetailsPage()
    {
        InitializeComponent();
        this.BackgroundColor = new Color(0, 0, 0, 0.6f);
    }

    public MainPage.PeerNodeCard? Card
    {
        get => _card;
        set
        {
            _card = value;
            BindingContext = _card;
        }
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnServiceClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn || _card is null)
            return;

        var serviceName = btn.Text;
        var peer = _card.ToPeerNode();

        // Grab Shell navigation BEFORE popping, so the reference stays valid
        var nav = Shell.Current.Navigation;

        // Close PeerDetailsPage modal, then the DiscoveryPopup underneath
        await nav.PopModalAsync(false);
        await nav.PopModalAsync(false);

        if (serviceName.StartsWith("Messaging", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.GoToAsync(nameof(MessagingPage),
                new Dictionary<string, object> { { "Peer", peer } });
        }
        else if (serviceName.StartsWith("Filesharing", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.GoToAsync(nameof(FileSharingPage),
                new Dictionary<string, object> { { "Peer", peer } });
        }
        else if (serviceName.StartsWith("LLMChat", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.GoToAsync(nameof(LLMChatPage),
                new Dictionary<string, object> { { "Peer", peer } });
        }
    }
}