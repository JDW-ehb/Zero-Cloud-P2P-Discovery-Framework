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

    public event Action? MessagesChanged;

    private Guid? _localPeerDbId;
    private ConversationItem? _activeConversation;
    private string? _activeProtocolPeerId;
    private bool _sessionReady;

    private const int MessagingPort = 5555;

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "Idle";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
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

        SendMessageCommand = new Command(async () => await SendAsync(), () => _sessionReady);

        _messaging.MessageReceived += OnMessageReceived;

        _ = InitAsync();
    }

    // 🔹 IMPORTANT: async Task, NOT async void
    public async Task ActivateConversationFromUIAsync(ConversationItem convo)
    {
        if (_activeConversation == convo)
            return;

        _activeConversation = convo;
        _activeProtocolPeerId = convo.Peer.ProtocolPeerId;
        _sessionReady = false;
        IsConnected = false;

        StatusMessage = $"Connecting to {convo.DisplayName}…";

        await _messaging.EnsureSessionAsync(
            convo.Peer.ProtocolPeerId,
            convo.Peer.IpAddress,
            MessagingPort);

        _sessionReady = true;
        IsConnected = true;
        StatusMessage = $"Chatting with {convo.DisplayName}";
        ((Command)SendMessageCommand).ChangeCanExecute();

        await LoadChatHistoryAsync(convo.Peer);
    }

    private async Task SendAsync()
    {
        if (!_sessionReady || _activeConversation == null)
            return;

        var text = OutgoingMessage.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var msg = new ChatMessage(
            id: Guid.NewGuid(), // temporary UI ID
            fromPeer: _peer.PeerId, // STRING
            toPeer: _activeProtocolPeerId!, // STRING
            content: text,
            direction: MessageDirection.Outgoing,
            timestamp: DateTime.UtcNow);

        // Optimistic UI update
        Messages.Add(msg);
        MessagesChanged?.Invoke();

        OutgoingMessage = string.Empty;

        await _messaging.SendMessageAsync(
            _activeConversation.Peer.ProtocolPeerId,
            _activeConversation.Peer.IpAddress,
            MessagingPort,
            text);
    }


    private void OnMessageReceived(ChatMessage msg)
    {
        if (msg.Direction == MessageDirection.Outgoing &&
            msg.FromPeer == _peer.PeerId)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var other = msg.FromPeer == _peer.PeerId
                ? msg.ToPeer
                : msg.FromPeer;

            UpdateConversationPreview(other, msg.Content, msg.Timestamp);

            if (_activeProtocolPeerId == null)
                return;

            if (msg.FromPeer != _activeProtocolPeerId &&
                msg.ToPeer != _activeProtocolPeerId)
                return;

            Messages.Add(msg);
            MessagesChanged?.Invoke();
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
        if (_localPeerDbId is null)
            return;

        Conversations.Clear();

        var peers = (await _chatQueries.GetPeersAsync())
            .Where(p => !p.IsLocal)
            .ToList();

        foreach (var peer in peers)
        {
            var item = new ConversationItem(peer);

            var last = await _chatQueries.GetLastMessageBetweenAsync(
                _localPeerDbId.Value,
                peer.PeerId);

            if (last != null)
            {
                item.LastMessage = last.Content;
                item.LastTimestamp = last.Timestamp;
            }

            Conversations.Add(item);
        }

        ResortConversations();
    }

    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        Messages.Clear();

        var history = await _chatQueries.GetHistoryAsync(
            _localPeerDbId!.Value,
            peer.PeerId);

        foreach (var msg in ChatMessageMapper.FromHistoryList(
                     history,
                     _localPeerDbId.Value,
                     _peer.PeerId,
                     peer.ProtocolPeerId))
        {
            Messages.Add(msg);
        }

        MessagesChanged?.Invoke();
    }

    private void UpdateConversationPreview(string protocolPeerId, string lastMessage, DateTime ts)
    {
        var convo = Conversations.FirstOrDefault(c => c.Peer.ProtocolPeerId == protocolPeerId);
        if (convo == null)
            return;

        convo.LastMessage = lastMessage;
        convo.LastTimestamp = ts;

        var idx = Conversations.IndexOf(convo);
        if (idx > 0)
            Conversations.Move(idx, 0);
    }

    private void ResortConversations()
    {
        if (Conversations.Count <= 1)
            return;

        var ordered = Conversations
            .OrderByDescending(c => c.LastTimestamp ?? DateTime.MinValue)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var currentIndex = Conversations.IndexOf(item);
            if (currentIndex != i)
                Conversations.Move(currentIndex, i);
        }
    }
}
