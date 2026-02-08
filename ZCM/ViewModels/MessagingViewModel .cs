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
    private bool _suppressSelection;

    public ConversationItem? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            // During reorder/restore selection: do NOT reload history
            if (_suppressSelection)
            {
                _selectedConversation = value;
                OnPropertyChanged();
                return;
            }

            // IMPORTANT: ignore transient null deselection caused by CollectionView refresh/reorder
            // (otherwise you clear Messages and it "reload flashes")
            if (value == null)
            {
                // If we already have a conversation selected, this null is almost certainly a UI glitch.
                if (_selectedConversation != null)
                    return;

                // Only allow a real "clear chat" if we truly had nothing selected.
                _activeProtocolPeerId = null;
                IsConnected = false;
                StatusMessage = "Idle";
                Messages.Clear();
                _selectedConversation = null;
                OnPropertyChanged();
                return;
            }

            var oldId = _selectedConversation?.Peer.ProtocolPeerId;
            var newId = value.Peer.ProtocolPeerId;
            if (oldId == newId)
                return;

            _selectedConversation = value;
            OnPropertyChanged();

            MainThread.BeginInvokeOnMainThread(async () => await OpenConversationAsync(value));
        }
    }

    private string _outgoingMessage = string.Empty;
    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set { _outgoingMessage = value; OnPropertyChanged(); }
    }

    public ICommand SendMessageCommand { get; }
    public ICommand RefreshCommand { get; }

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
            if (convo == null) return;

            var text = OutgoingMessage?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                //  Set this FIRST so SessionStarted won't think you're "not in a chat"
                _activeProtocolPeerId = convo.Peer.ProtocolPeerId;

                StatusMessage = $"Sending to {convo.DisplayName}...";

                await _messaging.SendMessageAsync(
                    remoteProtocolPeerId: convo.Peer.ProtocolPeerId,
                    remoteIp: convo.Peer.IpAddress,
                    port: MessagingPort,
                    content: text);

                OutgoingMessage = string.Empty;
                IsConnected = true;
                StatusMessage = $"Chatting with {convo.DisplayName}";
            }
            catch (Exception ex)
            {
                IsConnected = false;
                StatusMessage = $"Send failed: {ex.Message}";
            }
        });


        RefreshCommand = new Command(async () => await LoadConversationsAsync());

        _messaging.MessageReceived += msg =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                System.Diagnostics.Debug.WriteLine($">>> MessageReceived: {msg.Timestamp:HH:mm:ss} <<<");
                // Update preview + reorder left list
                var otherId = msg.FromPeer == _peer.PeerId ? msg.ToPeer : msg.FromPeer;
                UpdateConversationPreview(otherId, msg.Content, msg.Timestamp);

                // Only append to open chat
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = true;

                // Just update status if this is the current chat.
                if (_activeProtocolPeerId == remoteProtocolPeerId)
                {
                    var current = SelectedConversation;
                    StatusMessage = current != null
                        ? $"Chatting with {current.DisplayName}"
                        : $"Connected ({remoteProtocolPeerId})";
                }
                else
                {
                    // StatusMessage = "Session started in background", maybe we don't want that
                }
            });
        };


        _messaging.SessionClosed += () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = false;
                StatusMessage = "Disconnected";
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

    private async Task OpenConversationAsync(ConversationItem convo)
    {
        System.Diagnostics.Debug.WriteLine($">>> OpenConversationAsync: {convo.Peer.ProtocolPeerId} <<<");
        _activeProtocolPeerId = convo.Peer.ProtocolPeerId;
        IsConnected = false;
        StatusMessage = $"Opened {convo.DisplayName}";

        await LoadChatHistoryAsync(convo.Peer);
    }

    private async Task LoadConversationsAsync()
    {
        var peers = await _chatQueries.GetPeersAsync();
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

            var last = await _chatQueries.GetLastMessageBetweenAsync(_localPeerDbId.Value, peer.PeerId);
            if (last != null)
            {
                row.LastMessage = last.Content;
                row.LastTimestamp = last.Timestamp;
            }

            Conversations.Add(row);
        }

        ResortConversations();

        if (_activeProtocolPeerId != null)
        {
            var keep = Conversations.FirstOrDefault(c => c.Peer.ProtocolPeerId == _activeProtocolPeerId);
            if (keep != null)
            {
                _suppressSelection = true;
                SelectedConversation = keep;
                _suppressSelection = false;
            }
        }
    }

    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        System.Diagnostics.Debug.WriteLine(">>> LoadChatHistoryAsync CALLED <<<");
        if (_localPeerDbId is null)
        {
            Messages.Clear();
            return;
        }

        var history = await _chatQueries.GetHistoryAsync(_localPeerDbId.Value, peer.PeerId);

        Messages.Clear();
        foreach (var chatMessage in ChatMessageMapper.FromHistoryList(
                     history,
                     _localPeerDbId.Value,
                     _peer.PeerId,
                     peer.ProtocolPeerId))
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

        var idx = Conversations.IndexOf(convo);
        if (idx > 0)
        {
            _suppressSelection = true;
            Conversations.Move(idx, 0);
            _suppressSelection = false;
        }
    }

    private void ResortConversations()
    {
        if (Conversations.Count <= 1) return;

        var selectedId = _selectedConversation?.Peer.ProtocolPeerId;

        var ordered = Conversations
            .OrderByDescending(c => c.LastTimestamp ?? DateTime.MinValue)
            .ThenByDescending(c => c.Peer.LastSeen)
            .ToList();

        _suppressSelection = true;

        for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var item = ordered[targetIndex];
            var currentIndex = Conversations.IndexOf(item);
            if (currentIndex >= 0 && currentIndex != targetIndex)
                Conversations.Move(currentIndex, targetIndex);
        }

        // Restore selection *safely* (prevents UI null-blink from clearing Messages)
        if (selectedId != null)
        {
            var restored = Conversations.FirstOrDefault(c => c.Peer.ProtocolPeerId == selectedId);
            if (restored != null)
                SelectedConversation = restored;
        }

        _suppressSelection = false;
    }
}
