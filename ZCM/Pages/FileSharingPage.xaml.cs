using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class FileSharingPage : ContentPage
{
    public FileSharingPage(Guid sessionId)
    {
        InitializeComponent();

        BindingContext = new FileSharingViewModel(
            ServiceHelper.GetService<ZCL.Services.FileSharing.FileSharingService>(),
            sessionId);
    }
}
