using System.Diagnostics;
using ZCL.API;
namespace ZCM.Pages;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();

        BindingContext = Config.Instance;
    }

    private void SaveButton_Clicked(object sender, EventArgs e)
    {
        Debug.WriteLine("Saved.");
    }
}