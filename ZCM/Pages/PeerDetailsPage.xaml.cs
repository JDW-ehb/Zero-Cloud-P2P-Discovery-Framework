using ZCL.Models;
using ZCL.Security;
namespace ZCM.Pages;

[QueryProperty(nameof(Card), "Card")]
public partial class PeerDetailsPage : ContentPage
{



    private MainPage.PeerNodeCard? _card;

    public PeerDetailsPage()
    {
        InitializeComponent();
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
        => await Shell.Current.GoToAsync("..");

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("..");

    private async void OnServiceClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (_card is null)
            return;

        var peer = _card.ToPeerNode();

        switch (btn.Text)
        {
            case "Messaging":
                await Shell.Current.GoToAsync(
                    nameof(MessagingPage),
                    new Dictionary<string, object>
                    {
                        { "Peer", peer }
                    });
                break;

            case "FileTransfer":
                await Shell.Current.GoToAsync(
                    nameof(FileSharingPage),
                    new Dictionary<string, object>
                    {
                        { "Peer", peer }
                    });
                break;

            case "AIChat":
                await Shell.Current.GoToAsync(
                    nameof(LLMChatPage),
                    new Dictionary<string, object>
                    {
                        { "Peer", peer }
                    });
                break;
        }
    }
    private async void OnStartConnectionClicked(object sender, EventArgs e)
    {
        var secret = SecretEntry.Text?.Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            await DisplayAlert("Error", "Secret is required.", "OK");
            return;
        }

        var secretStore = ServiceHelper.GetService<ISharedSecretProvider>();

        secretStore.SetSecretAsync(secret);

        await DisplayAlert("Success", "Secret stored. Restart connection to apply.", "OK");
    }
}