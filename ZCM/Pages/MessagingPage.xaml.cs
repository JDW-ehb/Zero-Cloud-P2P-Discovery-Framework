using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
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
            ServiceHelper.GetService<IChatQueryService>());
    }

    private void OnConversationSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (BindingContext is not MessagingViewModel vm)
            return;

        if (e.CurrentSelection.Count == 0)
            return;

        if (e.CurrentSelection[0] is not ConversationItem convo)
            return;

        vm.ActivateConversationFromUI(convo);

        ((CollectionView)sender).SelectedItem = null;

        Dispatcher.DispatchDelayed(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                if (vm.Messages.Count > 0)
                {
                    MessagesView.ScrollTo(
                        vm.Messages[^1],
                        position: ScrollToPosition.End,
                        animate: false);
                }
            });

    }

}
