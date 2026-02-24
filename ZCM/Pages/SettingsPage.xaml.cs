using System.Diagnostics;
using ZCL.API;

namespace ZCM.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly Config _config;

    public SettingsPage()
    {
        InitializeComponent();

        // Use DI instead of static access
        _config = ServiceHelper.GetService<Config>();

        BindingContext = _config;
    }

    private async void SaveButton_Clicked(object sender, EventArgs e)
    {
        // If Config supports persistence, call it here.
        // Example:
        // _config.Save();

        Debug.WriteLine("Settings saved.");

        await Shell.Current.DisplayAlert(
            "Settings",
            "Configuration saved successfully.",
            "OK");
    }
}