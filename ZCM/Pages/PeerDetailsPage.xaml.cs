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

        var peer = _card.ToPeerNode();

        // Close popup first
        await Navigation.PopModalAsync(false);

        switch (btn.Text)
        {
            case "Messaging":
                await Shell.Current.GoToAsync(nameof(MessagingPage),
                    new Dictionary<string, object> { { "Peer", peer } });
                break;

            case "FileTransfer":
                await Shell.Current.GoToAsync(nameof(FileSharingPage),
                    new Dictionary<string, object> { { "Peer", peer } });
                break;

            case "AIChat":
                await Shell.Current.GoToAsync(nameof(LLMChatPage),
                    new Dictionary<string, object> { { "Peer", peer } });
                break;
        }
    }
}