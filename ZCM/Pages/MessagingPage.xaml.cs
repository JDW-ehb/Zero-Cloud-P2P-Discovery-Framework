using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class MessagingPage : ContentPage
{
    private bool _userNearBottom = true;

    private readonly MessagingViewModel _vm;

    public MessagingPage(PeerNode? openWithPeer = null)
    {
        InitializeComponent();

        _vm = new MessagingViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<MessagingService>(),
            ServiceHelper.GetService<IChatQueryService>());

        _vm.MessagesChanged += ScrollToBottomIfAllowed;
        BindingContext = _vm;

        if (openWithPeer != null)
        {
            Dispatcher.Dispatch(async () =>
            {
                var convo = _vm.Conversations
                    .FirstOrDefault(c => c.Peer.ProtocolPeerId == openWithPeer.ProtocolPeerId);

                if (convo == null)
                {
                    convo = new ConversationItem(openWithPeer);
                    _vm.Conversations.Insert(0, convo);
                }

                await _vm.ActivateConversationFromUIAsync(convo);
            });
        }
    }

    private async void OnConversationSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (BindingContext is not MessagingViewModel vm)
            return;

        if (e.CurrentSelection.Count == 0)
            return;

        if (e.CurrentSelection[0] is not ConversationItem convo)
            return;

        ((CollectionView)sender).SelectedItem = null;

        await vm.ActivateConversationFromUIAsync(convo);

        Dispatcher.DispatchDelayed(
            TimeSpan.FromMilliseconds(50),
            ScrollToBottomIfAllowed);
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
        _userNearBottom = remaining <= 2;
    }
}
