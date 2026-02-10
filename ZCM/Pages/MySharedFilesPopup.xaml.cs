using ZCM.ViewModels;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;

namespace ZCM.Pages;

public partial class MySharedFilesPopup : ContentPage
{
    private readonly FileSharingHubViewModel _vm;

    public MySharedFilesPopup(FileSharingHubViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync();

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync();

    private async void OnFilesDropped(object sender, DropEventArgs e)
    {
#if WINDOWS
    if (e.Data.Properties.TryGetValue("FileDrop", out var value) && value is string[] paths)
    {
        await _vm.AddFilesFromPathsAsync(paths);
    }
    else
    {
        System.Diagnostics.Debug.WriteLine("No file paths found in drop data.");
    }
#else
        System.Diagnostics.Debug.WriteLine("Drag & drop only implemented for Windows.");
#endif
    }



}
