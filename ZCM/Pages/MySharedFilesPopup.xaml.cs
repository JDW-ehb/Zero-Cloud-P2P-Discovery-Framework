using ZCM.ViewModels;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel.DataTransfer;

#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
#endif

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
    System.Diagnostics.Debug.WriteLine("DROP EVENT FIRED");

    if (e.Data.Properties.TryGetValue("StorageItems", out var value))
    {
        if (value is IEnumerable<object> items)
        {
            var paths = new List<string>();

            foreach (var item in items)
            {
                var pathProp = item.GetType().GetProperty("Path");
                if (pathProp != null)
                {
                    var path = pathProp.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }
            }

            if (paths.Count > 0)
            {
                await _vm.AddFilesFromPathsAsync(paths);
            }
        }
    }
#endif
    }




}
