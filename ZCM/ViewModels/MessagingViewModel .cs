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
    private string? _activeProtocolPeerId;

    private const int MessagingPort = 5555;

    // =====================
    // UI STATE
    // =====================

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

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private ConversationItem? _selectedConversation;
    public ConversationItem? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (_selectedConversation == value)
                return;

            _selectedConversation = value;
            OnPropertyChanged();

            if (value != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await OpenConversationAsync(value);
                });
            }
            else
            {
                _activeProtocolPeerId = null;
                IsConnected = false;
                StatusMessage = "Idle";
                Messages.Clear();
            }
        }
    }

    private string _outgoingMessage = string.Empty;
    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set { _outgoingMessage = value; OnPropertyChanged(); }
    }

    // =====================
    // COMMANDS
    // =====================

    public ICommand SendMessageCommand { get; }
    public ICommand RefreshCommand { get; }

    // =====================
    // CONSTRUCTOR
    // =====================

    public MessagingViewModel(
        ZcspPeer peer,
        MessagingService messaging,
        IChatQueryService chatQueries)
    {
        _peer = peer;
        _messaging = messaging;
        _chatQueries = chatQueries;

        SendMessageCommand = new Command(async () =>
        {
            var convo = SelectedConversation;
            if (convo == null)
                return;

            var text = OutgoingMessage?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                StatusMessage = $"Sending to {convo.DisplayName}...";
                // Core: auto-connect (if needed) + send
                await _messaging.SendMessageAsync(
                    remoteProtocolPeerId: convo.Peer.ProtocolPeerId,
                    remoteIp: convo.Peer.IpAddress,
                    port: MessagingPort,
                    content: text);

                OutgoingMessage = string.Empty;
                IsConnected = true;
                StatusMessage = $"Chatting with {convo.DisplayName}";
                _activeProtocolPeerId = convo.Peer.ProtocolPeerId;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                StatusMessage = $"Send failed: {ex.Message}";
            }
        });

        RefreshCommand = new Command(async () => await LoadConversationsAsync());

        // Incoming + outgoing events both come through here
        _messaging.MessageReceived += msg =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Update left list preview & order
                var otherId = msg.FromPeer == _peer.PeerId ? msg.ToPeer : msg.FromPeer;
                UpdateConversationPreview(otherId, msg.Content, msg.Timestamp);

                // Only show in the open chat
                if (_activeProtocolPeerId == null)
                    return;

                if (msg.FromPeer != _activeProtocolPeerId &&
                    msg.ToPeer != _activeProtocolPeerId)
                    return;

                Messages.Add(msg);
            });
        };

        _messaging.SessionStarted += remoteProtocolPeerId =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                _activeProtocolPeerId = remoteProtocolPeerId;
                IsConnected = true;

                // Make sure the peer exists in list (discovery might have just added it)
                await LoadConversationsAsync();

                SelectedConversation = Conversations
                    .FirstOrDefault(c => c.Peer.ProtocolPeerId == remoteProtocolPeerId);

                StatusMessage = SelectedConversation != null
                    ? $"Chatting with {SelectedConversation.DisplayName}"
                    : $"Connected (unknown peer: {remoteProtocolPeerId})";

                if (SelectedConversation != null)
                    await LoadChatHistoryAsync(SelectedConversation.Peer);
            });
        };

        _messaging.SessionClosed += () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = false;
                StatusMessage = "Disconnected";
                // Keep _activeProtocolPeerId so UI can still show history;
                // if you want to “close chat”, set it to null.
            });
        };

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        _localPeerDbId = await _chatQueries.GetLocalPeerIdAsync();
        await LoadConversationsAsync();
        StatusMessage = "Ready";
    }

    // =====================
    // HELPERS
    // =====================

    private async Task OpenConversationAsync(ConversationItem convo)
    {
        _activeProtocolPeerId = convo.Peer.ProtocolPeerId;
        IsConnected = false; // we only know after we try sending or session starts
        StatusMessage = $"Opened {convo.DisplayName}";

        await LoadChatHistoryAsync(convo.Peer);
    }

    private async Task LoadConversationsAsync()
    {
        var peers = await _chatQueries.GetPeersAsync();

        // Hide local peer from list
        peers = peers.Where(p => !p.IsLocal).ToList();

        Conversations.Clear();

        if (_localPeerDbId is null)
        {
            StatusMessage = "Local peer not found";
            return;
        }

        foreach (var peer in peers)
        {
            var row = new ConversationItem(peer);

            // last message preview (you said you want this)
            var last = await _chatQueries.GetLastMessageBetweenAsync(_localPeerDbId.Value, peer.PeerId);
            if (last != null)
            {
                row.LastMessage = last.Content;
                row.LastTimestamp = last.Timestamp;
            }

            Conversations.Add(row);
        }

        ResortConversations();

        // Keep selection stable
        if (_activeProtocolPeerId != null)
        {
            SelectedConversation = Conversations
                .FirstOrDefault(c => c.Peer.ProtocolPeerId == _activeProtocolPeerId);
        }
    }

    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        if (_localPeerDbId is null)
        {
            Messages.Clear();
            return;
        }

        var history = await _chatQueries.GetHistoryAsync(_localPeerDbId.Value, peer.PeerId);

        Messages.Clear();

        foreach (var chatMessage in ChatMessageMapper.FromHistoryList(
                     history,
                     _localPeerDbId.Value,   // local DB peer guid (PeerId)
                     _peer.PeerId,           // local protocol GUID string
                     peer.ProtocolPeerId))   // remote protocol GUID string
        {
            Messages.Add(chatMessage);
        }
    }

    private void UpdateConversationPreview(string protocolPeerId, string lastMessage, DateTime ts)
    {
        var convo = Conversations.FirstOrDefault(c => c.Peer.ProtocolPeerId == protocolPeerId);
        if (convo == null)
            return;

        convo.LastMessage = lastMessage;
        convo.LastTimestamp = ts;

        ResortConversations();
    }

    private void ResortConversations()
    {
        var ordered = Conversations
            .OrderByDescending(c => c.LastTimestamp ?? DateTime.MinValue)
            .ThenByDescending(c => c.Peer.LastSeen)
            .ToList();

        Conversations.Clear();
        foreach (var c in ordered)
            Conversations.Add(c);
    }
}
