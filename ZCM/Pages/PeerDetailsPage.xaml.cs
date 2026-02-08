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

        if (btn.Text != "Messaging")
            return;

        // 1?? Close popup
        await Navigation.PopModalAsync();

        // 2?? Navigate to MessagingPage with peer
        await Application.Current!.MainPage!.Navigation
            .PushAsync(new MessagingPage(card.ToPeerNode()));
    }


}
