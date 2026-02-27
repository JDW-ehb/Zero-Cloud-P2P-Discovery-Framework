using ZCL.Security;
using ZCM.Notifications;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage()
    {
        InitializeComponent();

        _vm = new SettingsViewModel();
        BindingContext = _vm;

        Loaded += async (_, _) => await _vm.LoadDraftAsync();
    }

    private async void SaveButton_Clicked(object sender, EventArgs e)
    {
        var cache = ServiceHelper.GetService<TrustGroupCache>();

        await _vm.SaveAllAsync(cache);

        await Navigation.PopModalAsync(false);

        await TransientNotificationService.ShowAsync(
            "Settings saved successfully.",
            NotificationSeverity.Success,
            2000);
    }

    private async void GroupsButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new GroupsPopup(_vm), false);
    }

    private async void ServicesButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new AnnouncedServicesPopup(_vm), false);
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);
}