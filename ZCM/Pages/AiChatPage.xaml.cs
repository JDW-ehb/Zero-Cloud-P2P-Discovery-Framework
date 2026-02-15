using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.AI;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class AiChatPage : ContentPage
{
    private readonly AiChatViewModel _vm;

    public AiChatPage(PeerNode? preselectPeer = null)
    {
        InitializeComponent();

        _vm = new AiChatViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<AiChatService>());

        BindingContext = _vm;

        _ = InitializeAsync(preselectPeer);
    }

    private async Task InitializeAsync(PeerNode? preselectPeer)
    {
        await _vm.InitializeAsync();

        if (preselectPeer != null)
            await _vm.ActivatePeerAsync(preselectPeer);
    }


}
