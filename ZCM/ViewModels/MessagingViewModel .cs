using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;
using ZCL.Services.Messaging.ZCL.Services.Messaging;

namespace ZCM.ViewModels;

public class MessagingViewModel : BindableObject
{
    private readonly ZcspPeer _peer;
    private readonly MessagingService _messaging;
    private readonly ServiceDBContext _db;

    // =====================
    // UI STATE
    // =====================

    private bool _isHosting;
    public bool IsHosting
    {
        get => _isHosting;
        set
        {
            _isHosting = value;
            OnPropertyChanged();
        }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            _isConnected = value;
            OnPropertyChanged();
        }
    }


    private string _statusMessage = "Idle";
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
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
        set
        {
            _outgoingMessage = value;
            OnPropertyChanged();
        }
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
        ServiceDBContext db)
    {
        _peer = peer;
        _messaging = messaging;
        _db = db;

        LoadPeers();

        HostCommand = new Command(async () =>
        {
            if (IsHosting)
                return;

            try
            {
                IsHosting = true;
                StatusMessage = "Hosting started on port 5555";

                await _peer.StartHostingAsync(
                    5555,
                    name => name == _messaging.ServiceName ? _messaging : null
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
                IsConnected = true;   // 👈 THIS is the key line
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
                if (SelectedPeer == null)
                    return;

                var selectedId = SelectedPeer.ProtocolPeerId;

                if (msg.FromPeer == selectedId || msg.ToPeer == selectedId)
                {
                    Messages.Add(msg);
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

    }

    // =====================
    // HELPERS
    // =====================

    private void LoadPeers()
    {
        var peers = _db.Peers
            .Where(p => p.ProtocolPeerId != _peer.PeerId) // exclude local
            .OrderByDescending(p => p.LastSeen)
            .ToList();


        var previouslySelectedPeerId = SelectedPeer?.PeerId;

        AvailablePeers.Clear();
        foreach (var peer in peers)
            AvailablePeers.Add(peer);

        if (previouslySelectedPeerId != null)
        {
            SelectedPeer = AvailablePeers
                .FirstOrDefault(p => p.PeerId == previouslySelectedPeerId);
        }
    }


    private async Task LoadChatHistoryAsync(PeerNode peer)
    {
        var localPeerGuid = await _db.Peers
            .Where(p => p.ProtocolPeerId == _peer.PeerId)
            .Select(p => p.PeerId)
            .FirstOrDefaultAsync();

        if (localPeerGuid == Guid.Empty)
        {
            // No local peer record yet, no history possible
            Messages.Clear();
            return;
        }

        var remotePeerGuid = peer.PeerId;


        var history = await _db.Messages
            .Where(m =>
                (m.FromPeerId == localPeerGuid && m.ToPeerId == remotePeerGuid) ||
                (m.FromPeerId == remotePeerGuid && m.ToPeerId == localPeerGuid))
            .OrderBy(m => m.Timestamp)
            .ToListAsync();

        Messages.Clear();

        foreach (var msg in history)
        {
            var isOutgoing = msg.FromPeerId == localPeerGuid;

            Messages.Add(new ChatMessage(
                fromPeer: isOutgoing ? _peer.PeerId : peer.ProtocolPeerId,
                toPeer: isOutgoing ? peer.ProtocolPeerId : _peer.PeerId,
                content: msg.Content,
                direction: isOutgoing
                    ? MessageDirection.Outgoing
                    : MessageDirection.Incoming
            ));
        }

    }


}
