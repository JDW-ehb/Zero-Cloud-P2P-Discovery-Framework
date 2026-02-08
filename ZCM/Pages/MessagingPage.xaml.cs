using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class MessagingPage : ContentPage
{
    private bool _userNearBottom = true;

    public MessagingPage()
    {
        InitializeComponent();

        var vm = new MessagingViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<MessagingService>(),
            ServiceHelper.GetService<IChatQueryService>());

        vm.MessagesChanged += OnMessagesChanged;

        BindingContext = vm;
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

        // Scroll to bottom after history load
        Dispatcher.DispatchDelayed(
            TimeSpan.FromMilliseconds(50),
            ScrollToBottomIfAllowed);
    }

    private void OnMessagesChanged()
    {
        ScrollToBottomIfAllowed();
    }

    private void ScrollToBottomIfAllowed()
    {
        if (!_userNearBottom)
            return;

        if (BindingContext is not MessagingViewModel vm)
            return;

        if (vm.Messages.Count == 0)
            return;

        Dispatcher.Dispatch(() =>
        {
            MessagesView.ScrollTo(
                vm.Messages[^1],
                position: ScrollToPosition.End,
                animate: true);
        });
    }

    private void OnMessagesScrolled(object sender, ItemsViewScrolledEventArgs e)
    {
        if (BindingContext is not MessagingViewModel vm)
            return;

        if (vm.Messages.Count == 0)
            return;

        var remaining = vm.Messages.Count - (e.LastVisibleItemIndex + 1);

        // Near bottom if last 2 items are visible
        _userNearBottom = remaining <= 2;
    }
}
