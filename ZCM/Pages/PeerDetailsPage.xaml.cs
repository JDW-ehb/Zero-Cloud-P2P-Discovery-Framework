using ZCL.Models;

namespace ZCM.Pages;

public partial class PeerDetailsPage : ContentPage
{
    public PeerDetailsPage(object bindingContext)
    {
        InitializeComponent();
        BindingContext = bindingContext;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync();

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync();

    private async void OnServiceClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (BindingContext is not MainPage.PeerNodeCard card)
            return;

        var peer = card.ToPeerNode();

        // Close modal FIRST
        await Navigation.PopModalAsync();

        // Always navigate from root navigation stack
        var nav = Application.Current!.MainPage!.Navigation;

        switch (btn.Text)
        {
            case "Messaging":
                await nav.PushAsync(new MessagingPage(peer));
                break;

            case "FileTransfer":
                await nav.PushAsync(new FileSharingPage(peer));
                break;

            case "AIChat":
                // future extension
                break;
        }
    }
}
