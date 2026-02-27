using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class AnnouncedServicesPopup : ContentPage
{
    public AnnouncedServicesPopup(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);
}