using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCM.Notifications;

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

        SendMessageCommand =
            new Command(async () => await SendAsync(), () => _sessionReady);

        _messaging.MessageReceived += OnMessageReceived;
        _messaging.SessionStarted += OnSessionStarted;
        _messaging.SessionClosed += OnSessionClosed;

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        _localPeerDbId = await _chatQueries.GetLocalPeerIdAsync();
        await LoadConversationsAsync();
        StatusMessage = "Ready";
    }

    private void OnSessionStarted(string remoteProtocolPeerId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_activeProtocolPeerId != remoteProtocolPeerId)
                return;

            _sessionReady = true;
            IsConnected = true;
            StatusMessage = "Connected";
            ((Command)SendMessageCommand).ChangeCanExecute();
        });
    }

    private void OnSessionClosed(string remoteProtocolPeerId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_activeProtocolPeerId != remoteProtocolPeerId)
                return;

            _sessionReady = false;
            IsConnected = false;
            StatusMessage = "Peer disconnected.";

            await TransientNotificationService.ShowAsync(
                "Peer disconnected.",
                NotificationSeverity.Warning,
                5000);

            ((Command)SendMessageCommand).ChangeCanExecute();
        });
    }

    public async Task ActivateConversationFromUIAsync(ConversationItem convo)
    {
        if (_activeConversation == convo)
            return;

        _activeConversation = convo;
        _activeProtocolPeerId = convo.Peer.ProtocolPeerId;
        _sessionReady = false;
        IsConnected = false;

        StatusMessage = $"Connecting to {convo.DisplayName}…";

        try
        {
            await _messaging.EnsureSessionAsync(
                convo.Peer.ProtocolPeerId,
                convo.Peer.IpAddress,
                MessagingPort);
        }
        catch
        {
            await TransientNotificationService.ShowAsync(
                "Peer is not available.",
                NotificationSeverity.Warning);

            StatusMessage = "Connection failed.";
            return;
        }

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
            Guid.NewGuid(),
            _peer.PeerId,
            _activeProtocolPeerId!,
            text,
            MessageDirection.Outgoing,
            DateTime.UtcNow);

        Messages.Add(msg);
        MessagesChanged?.Invoke();
        OutgoingMessage = string.Empty;

        try
        {
            await _messaging.SendMessageAsync(
                _activeConversation.Peer.ProtocolPeerId,
                _activeConversation.Peer.IpAddress,
                MessagingPort,
                text);
        }
        catch
        {
            _sessionReady = false;
            IsConnected = false;

            await TransientNotificationService.ShowAsync(
                "Connection lost.",
                NotificationSeverity.Error);

            ((Command)SendMessageCommand).ChangeCanExecute();
        }
    }

    private void OnMessageReceived(ChatMessage msg)
    {
        if (msg.Direction == MessageDirection.Outgoing &&
            msg.FromPeer == _peer.PeerId)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_activeProtocolPeerId == null)
                return;

            if (msg.FromPeer != _activeProtocolPeerId &&
                msg.ToPeer != _activeProtocolPeerId)
                return;

            Messages.Add(msg);
            MessagesChanged?.Invoke();
        });
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
            Conversations.Add(new ConversationItem(peer));
        }
    }

    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        if (_localPeerDbId is null)
            return;

        Messages.Clear();

        var history = await _chatQueries.GetHistoryAsync(
            _localPeerDbId.Value,
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
}
