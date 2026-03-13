using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.API;
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
    private readonly DataStore _store;

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
        IChatQueryService chatQueries,
        DataStore store)
    {
        _peer = peer;
        _messaging = messaging;
        _chatQueries = chatQueries;
        _store = store;

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

        await Task.Delay(500);

        var server = _store.Peers.FirstOrDefault(p => p.Role == NodeRole.Server);

        if (server != null)
        {
            try
            {
                await _messaging.EnsureSessionAsync(server.ProtocolPeerId);

                StatusMessage = "Connected to server";
            }
            catch
            {
                StatusMessage = "Server offline";
            }
        }
        else
        {
            StatusMessage = "No server discovered";
        }
    }


    private void OnSessionStarted(string remoteProtocolPeerId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_activeProtocolPeerId == remoteProtocolPeerId)
            {
                SetSessionConnected();
            }

        });
    }

    private void OnSessionClosed(string remoteProtocolPeerId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_activeProtocolPeerId != remoteProtocolPeerId)
                return;

            SetSessionDisconnected("Peer disconnected.");

            await TransientNotificationService.ShowAsync(
                "Peer disconnected.",
                NotificationSeverity.Warning,
                5000);
        });
    }

    private void SetSessionConnected()
    {
        _sessionReady = true;
        IsConnected = true;
        StatusMessage = "Connected";
        ((Command)SendMessageCommand).ChangeCanExecute();
    }

    private void SetSessionDisconnected(string status)
    {
        _sessionReady = false;
        IsConnected = false;
        StatusMessage = status;
        ((Command)SendMessageCommand).ChangeCanExecute();
    }


    public async Task ActivateConversationFromUIAsync(ConversationItem convo)
    {
        if (_activeConversation == convo)
            return;

        _activeConversation = convo;
        _activeProtocolPeerId = convo.Peer.ProtocolPeerId;

        SetSessionDisconnected("Connecting…");

        await LoadChatHistoryAsync(convo.Peer);

        try
        {
            await _messaging.EnsureSessionAsync(
                convo.Peer.ProtocolPeerId);

            SetSessionConnected();
        }
        catch
        {
            SetSessionDisconnected("Offline (history loaded).");

            await TransientNotificationService.ShowAsync(
                "Peer is not available (showing history).",
                NotificationSeverity.Warning);
        }
    }



    private async Task SendAsync()
    {
        if (!_sessionReady || _activeConversation == null)
            return;

        var text = OutgoingMessage.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        OutgoingMessage = string.Empty;

        try
        {
            await _messaging.SendMessageAsync(
                _activeConversation.Peer.ProtocolPeerId,
                text);
        }
        catch
        {
            SetSessionDisconnected("Connection lost.");

            await TransientNotificationService.ShowAsync(
                "Connection lost.",
                NotificationSeverity.Error);
        }
    }


    private void OnMessageReceived(ChatMessage msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Always refresh preview text/time in the left conversation list.
            UpdateConversationPreview(msg);

            if (_activeProtocolPeerId == null)
                return;

            if (msg.FromPeer != _activeProtocolPeerId &&
                msg.ToPeer != _activeProtocolPeerId)
                return;

            Messages.Add(msg);
            MessagesChanged?.Invoke();
        });
    }

    private void UpdateConversationPreview(ChatMessage msg)
    {
        // Identify the remote peer for this message.
        var remoteProtocolPeerId = msg.FromPeer == _peer.PeerId
            ? msg.ToPeer
            : msg.FromPeer;

        var convo = Conversations.FirstOrDefault(c =>
            c.Peer.ProtocolPeerId == remoteProtocolPeerId);

        if (convo == null)
            return;

        convo.LastMessage = msg.Content;
        convo.LastTimestamp = msg.Timestamp;

        // Optional UX: move most recently active conversation to top.
        var currentIndex = Conversations.IndexOf(convo);
        if (currentIndex > 0)
        {
            Conversations.RemoveAt(currentIndex);
            Conversations.Insert(0, convo);
        }
    }


    private async Task LoadConversationsAsync()
    {
        var historyPeers = await _chatQueries.GetPeersWithMessagesAsync();

        Conversations.Clear();

        if (_localPeerDbId is null)
            return;

        foreach (var peer in historyPeers)
        {
            var convo = new ConversationItem(peer);

            var last = await _chatQueries.GetLastMessageBetweenAsync(
                _localPeerDbId.Value,
                peer.PeerId);

            if (last != null)
            {
                convo.LastMessage = last.Content;
                convo.LastTimestamp = last.Timestamp;
            }

            Conversations.Add(convo);
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
