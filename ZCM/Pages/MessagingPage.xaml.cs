using ZCM.ViewModels;
using ZCL.Services.Messaging;

namespace ZCM.Pages;

public partial class MessagingPage : ContentPage
{
    public MessagingPage()
    {
        InitializeComponent();

        var messaging = ServiceHelper.GetService<MessagingService>();
        BindingContext = new MessagingViewModel(messaging);
    }
}
