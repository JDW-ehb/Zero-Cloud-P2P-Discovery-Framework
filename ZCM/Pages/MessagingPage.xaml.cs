using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCL.Models;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class MessagingPage : ContentPage
{
    public MessagingPage()
    {
        InitializeComponent();

        BindingContext = new MessagingViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<MessagingService>(),
            ServiceHelper.GetService<ServiceDBContext>()
        );
    }
}
