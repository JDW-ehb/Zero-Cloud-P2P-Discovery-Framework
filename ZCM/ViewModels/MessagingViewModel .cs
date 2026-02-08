using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;

namespace ZCM.ViewModels;

public sealed class MessagingViewModel : BindableObject
{
    private readonly ZcspPeer _peer;
    private readonly MessagingService _messaging;
    private readonly IChatQueryService _chatQueries;

    private Guid? _localPeerDbId;
    private ConversationItem? _activeConversation;
    private string? _activeProtocolPeerId;

    private const int MessagingPort = 5555;

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "Idle";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private string _outgoingMessage = string.Empty;
    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set { _outgoingMessage = value; OnPropertyChanged(); }
    }

    public ICommand SendMessageCommand { get; }

    public MessagingViewModel(
        ZcspPeer peer,
        MessagingService messaging,
        IChatQueryService chatQueries)
    {
        _peer = peer;
        _messaging = messaging;
        _chatQueries = chatQueries;

        SendMessageCommand = new Command(async () => await SendAsync());

        _messaging.MessageReceived += OnMessageReceived;

        _ = InitAsync();
    }

    public void ActivateConversationFromUI(ConversationItem convo)
    {
        if (_activeConversation == convo)
            return;

        _activeConversation = convo;
        _activeProtocolPeerId = convo.Peer.ProtocolPeerId;

        _ = LoadChatHistoryAsync(convo.Peer);
    }

    private async Task SendAsync()
    {
        if (_activeConversation == null)
            return;

        var text = OutgoingMessage.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        await _messaging.SendMessageAsync(
            _activeConversation.Peer.ProtocolPeerId,
            _activeConversation.Peer.IpAddress,
            MessagingPort,
            text);

        OutgoingMessage = string.Empty;
    }

    private void OnMessageReceived(ChatMessage msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var other = msg.FromPeer == _peer.PeerId ? msg.ToPeer : msg.FromPeer;
            UpdateConversationPreview(other, msg.Content, msg.Timestamp);

            if (_activeProtocolPeerId == null)
                return;

            if (msg.FromPeer != _activeProtocolPeerId &&
                msg.ToPeer != _activeProtocolPeerId)
                return;

            Messages.Add(msg);
        });
    }

    private async Task InitAsync()
    {
        _localPeerDbId = await _chatQueries.GetLocalPeerIdAsync();
        await LoadConversationsAsync();
        StatusMessage = "Ready";
    }

    private async Task LoadConversationsAsync()
    {
        var peers = (await _chatQueries.GetPeersAsync())
            .Where(p => !p.IsLocal);

        foreach (var peer in peers)
            Conversations.Add(new ConversationItem(peer));
    }

    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        Messages.Clear();

        var history = await _chatQueries.GetHistoryAsync(
            _localPeerDbId!.Value, peer.PeerId);

        foreach (var msg in ChatMessageMapper.FromHistoryList(
                     history, _localPeerDbId.Value, _peer.PeerId, peer.ProtocolPeerId))
            Messages.Add(msg);
    }

    private void UpdateConversationPreview(string protocolPeerId, string lastMessage, DateTime ts)
    {
        var convo = Conversations.FirstOrDefault(c =>
            c.Peer.ProtocolPeerId == protocolPeerId);

        if (convo == null)
            return;

        convo.LastMessage = lastMessage;
        convo.LastTimestamp = ts;
    }
}
