using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class MessagingPage : ContentPage
{
    public MessagingPage()
    {
        InitializeComponent();

        var messaging = ServiceHelper.GetService<MessagingService>();
        BindingContext = new MessagingViewModel(
                    ServiceHelper.GetService<ZcspPeer>(),
                    ServiceHelper.GetService<MessagingService>()
                );
    }
}
