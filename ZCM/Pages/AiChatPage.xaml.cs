using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.AI;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class AiChatPage : ContentPage
{
    private readonly AiChatViewModel _vm;

    public AiChatPage()
    {
        InitializeComponent();

        _vm = new AiChatViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<AiChatService>());

        BindingContext = _vm;

        _ = _vm.InitializeAsync();
    }

}
