using Microsoft.EntityFrameworkCore;
using ZCL.Models;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class AnnouncedServicesPopup : ContentPage
{
    private readonly SettingsViewModel _vm;

    public AnnouncedServicesPopup(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        db.AnnouncedServiceSettings.RemoveRange(db.AnnouncedServiceSettings);
        await db.SaveChangesAsync();

        db.AnnouncedServiceSettings.AddRange(
            _vm.AnnouncedServices.Select(s => new AnnouncedServiceSettingEntity
            {
                ServiceName = s.ServiceName,
                IsEnabled = s.IsEnabled
            }));

        await db.SaveChangesAsync();

        await Navigation.PopModalAsync(false);
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }

    private async void OnBackdropTapped(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }
}