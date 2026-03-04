using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class NetworkSettingsPopup : ContentPage
{
    private readonly SettingsViewModel _vm;

    public NetworkSettingsPopup(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }

    private async void OnBackdropTapped(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }
}