using ZCM.ViewModels;
using ZCL.Models;

namespace ZCM.Pages;

[QueryProperty(nameof(Peer), "Peer")]
public partial class FileSharingPage : ContentPage
{
    private readonly FileSharingHubViewModel _vm;

    private PeerNode? _preselectPeer;

    public FileSharingPage()
    {
        InitializeComponent();

        _vm = new FileSharingHubViewModel(
            ServiceHelper.GetService<ZCL.Services.FileSharing.FileSharingService>(),
            ServiceHelper.GetService<ZCL.Repositories.Peers.IPeerRepository>());

        BindingContext = _vm;
    }

    public PeerNode? Peer
    {
        get => _preselectPeer;
        set
        {
            _preselectPeer = value;

            if (_preselectPeer == null)
                return;

            Dispatcher.Dispatch(async () =>
            {
                await Task.Delay(50); // allow UI to render
                await _vm.ActivatePeerAsync(_preselectPeer);
            });
        }
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
        // Modal popup is fine here — it’s a true popup
        await Navigation.PushModalAsync(
            new MySharedFilesPopup(_vm));
    }
}