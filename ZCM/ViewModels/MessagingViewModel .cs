using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;

namespace ZCM.ViewModels;

public class MessagingViewModel : BindableObject
{
    private readonly ZcspPeer _peer;
    private readonly MessagingService _messaging;
    private readonly IChatQueryService _chatQueries;

    private string? _activeProtocolPeerId;

    // =====================
    // UI STATE
    // =====================

    private bool _isHosting;
    public bool IsHosting
    {
        get => _isHosting;
        set { _isHosting = value; OnPropertyChanged(); }
    }

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

    public ObservableCollection<PeerNode> AvailablePeers { get; } = new();

    private PeerNode? _selectedPeer;
    public PeerNode? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            _selectedPeer = value;
            OnPropertyChanged();

            if (value != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    Messages.Clear();
                    await LoadChatHistoryAsync(value);
                });
            }
        }
    }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private string _outgoingMessage = string.Empty;
    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set { _outgoingMessage = value; OnPropertyChanged(); }
    }

    // =====================
    // COMMANDS
    // =====================

    public ICommand HostCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand SendMessageCommand { get; }

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

        HostCommand = new Command(async () =>
        {
            if (IsHosting)
                return;

            try
            {
                IsHosting = true;
                StatusMessage = "Hosting started on port 5555";

                _ = Task.Run(() =>
                    _peer.StartHostingAsync(
                        5555,
                        name => name == _messaging.ServiceName ? _messaging : null
                    )
                );
            }
            catch (Exception ex)
            {
                IsHosting = false;
                StatusMessage = $"Hosting failed: {ex.Message}";
            }
        });

        ConnectCommand = new Command(async () =>
        {
            if (SelectedPeer == null)
            {
                StatusMessage = "No peer selected";
                IsConnected = false;
                return;
            }

            try
            {
                StatusMessage = $"Connecting to {SelectedPeer.HostName} ({SelectedPeer.IpAddress})...";
                IsConnected = false;

                await _messaging.ConnectToPeerAsync(
                    SelectedPeer.IpAddress,
                    5555,
                    SelectedPeer.ProtocolPeerId
                );

                StatusMessage = $"Connected to {SelectedPeer.HostName}";
                IsConnected = true;

                // Keep the "active session peer" consistent
                _activeProtocolPeerId = SelectedPeer.ProtocolPeerId;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Connection failed: {ex.Message}";
                IsConnected = false;
            }
        });

        SendMessageCommand = new Command(async () =>
        {
            if (string.IsNullOrWhiteSpace(OutgoingMessage))
                return;

            await _messaging.SendMessageAsync(OutgoingMessage);
            OutgoingMessage = string.Empty;
        });

        _messaging.MessageReceived += msg =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_activeProtocolPeerId == null)
                    return;

                // Only show messages for the currently active conversation
                if (msg.FromPeer != _activeProtocolPeerId &&
                    msg.ToPeer != _activeProtocolPeerId)
                    return;

                // Ensure UI selection matches session (optional but nice)
                if (SelectedPeer == null || SelectedPeer.ProtocolPeerId != _activeProtocolPeerId)
                    SelectedPeer = AvailablePeers.FirstOrDefault(p => p.ProtocolPeerId == _activeProtocolPeerId);

                Messages.Add(msg);
            });
        };

        _messaging.SessionStarted += remoteProtocolPeerId =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                _activeProtocolPeerId = remoteProtocolPeerId;

                // refresh list (in case discovery just added them)
                await LoadPeersAsync();

                // auto-select the peer that matches the session
                SelectedPeer = AvailablePeers.FirstOrDefault(p => p.ProtocolPeerId == remoteProtocolPeerId);

                IsConnected = SelectedPeer != null;
                StatusMessage = IsConnected
                    ? $"Connected to {SelectedPeer!.HostName}"
                    : $"Connected (unknown peer: {remoteProtocolPeerId})";

                // load history so session messages stack on top
                if (SelectedPeer != null)
                    await LoadChatHistoryAsync(SelectedPeer);
            });
        };

        _messaging.SessionClosed += () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _activeProtocolPeerId = null;
                IsConnected = false;
                StatusMessage = "Disconnected";
            });
        };

        // Initial load
        _ = LoadPeersAsync();
    }

    // =====================
    // HELPERS
    // =====================

    private async Task LoadPeersAsync()
    {
        var peers = await _chatQueries.GetPeersAsync();
        var previouslySelectedPeerId = SelectedPeer?.PeerId;

        AvailablePeers.Clear();
        foreach (var peer in peers)
            AvailablePeers.Add(peer);

        if (previouslySelectedPeerId != null)
            SelectedPeer = AvailablePeers.FirstOrDefault(p => p.PeerId == previouslySelectedPeerId);
    }

    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        var localPeerId = await _chatQueries.GetLocalPeerIdAsync();
        if (localPeerId is null)
        {
            Messages.Clear();
            return;
        }

        var history = await _chatQueries.GetHistoryAsync(localPeerId.Value, peer.PeerId);

        Messages.Clear();

        foreach (var chatMessage in ChatMessageMapper.FromHistoryList(
                     history,
                     localPeerId.Value,
                     _peer.PeerId,
                     peer.ProtocolPeerId))
        {
            Messages.Add(chatMessage);
        }
    }
}
