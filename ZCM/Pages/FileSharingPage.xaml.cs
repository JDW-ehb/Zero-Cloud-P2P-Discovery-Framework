using ZCM.ViewModels;
using ZCL.Models;

namespace ZCM.Pages;

public partial class FileSharingPage : ContentPage
{
    private readonly FileSharingHubViewModel _vm;

    public FileSharingPage()
    {
        InitializeComponent();

        _vm = new FileSharingHubViewModel(
            ServiceHelper.GetService<ZCL.Services.FileSharing.FileSharingService>(),
            ServiceHelper.GetService<ZCL.Repositories.Peers.IPeerRepository>());

        BindingContext = _vm;
    }

    // ?? New constructor to support redirection from PeerDetailsPage
    public FileSharingPage(PeerNode peer) : this()
    {
        _ = ActivatePeerOnLoad(peer);
    }

    private async Task ActivatePeerOnLoad(PeerNode peer)
    {
        // Let UI render first
        await Task.Delay(50);
        await _vm.ActivatePeerAsync(peer);
    }

    private async void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count == 0)
            return;

        if (e.CurrentSelection[0] is not PeerNode peer)
            return;

        ((CollectionView)sender).SelectedItem = null;
        await _vm.ActivatePeerAsync(peer);
    }

    private async void OnMySharedFilesClicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(
            new MySharedFilesPopup(_vm));
    }
}
