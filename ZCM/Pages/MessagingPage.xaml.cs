using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCM.ViewModels;

namespace ZCM.Pages;

[QueryProperty(nameof(Peer), "Peer")]
public partial class MessagingPage : ContentPage
{
    private bool _userNearBottom = true;

    private readonly MessagingViewModel _vm;

    private PeerNode? _openWithPeer;

    public MessagingPage()
    {
        InitializeComponent();

        _vm = new MessagingViewModel(
           ServiceHelper.GetService<ZcspPeer>(),
           ServiceHelper.GetService<MessagingService>(),
           ServiceHelper.GetService<IChatQueryService>(),
           ServiceHelper.GetService<DataStore>());

        _vm.MessagesChanged += ScrollToBottomIfAllowed;
        BindingContext = _vm;
    }

    public PeerNode? Peer
    {
        get => _openWithPeer;
        set
        {
            _openWithPeer = value;

            if (_openWithPeer == null)
                return;

            Dispatcher.Dispatch(async () =>
            {
                var convo = _vm.Conversations
                    .FirstOrDefault(c =>
                        c.Peer.ProtocolPeerId == _openWithPeer.ProtocolPeerId);

                if (convo == null)
                {
                    convo = new ConversationItem(_openWithPeer);
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

    private void OnMessageEntryCompleted(object sender, EventArgs e)
    {
        if (BindingContext is MessagingViewModel vm &&
            vm.SendMessageCommand.CanExecute(null))
        {
            vm.SendMessageCommand.Execute(null);
        }
    }
}