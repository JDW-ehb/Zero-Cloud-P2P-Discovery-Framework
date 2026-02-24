using System.Diagnostics;
using ZCL.API;

namespace ZCM.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly Config _config;

    public SettingsPage()
    {
        InitializeComponent();

        _config = ServiceHelper.GetService<Config>();

        BindingContext = _config;
    }

    private async void SaveButton_Clicked(object sender, EventArgs e)
    {

        Debug.WriteLine("Settings saved.");

        await Shell.Current.DisplayAlert(
            "Settings",
            "Configuration saved successfully.",
            "OK");
    }
}