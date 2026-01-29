using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Dispatching;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.Messaging;

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

            if (_selectedPeer != null)
                _ = LoadHistoryAsync(_selectedPeer);
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
                return;
            }

            try
            {
                StatusMessage = $"Connecting to {SelectedPeer.HostName} ({SelectedPeer.IpAddress})...";

                await _messaging.ConnectToPeerAsync(
                    SelectedPeer.IpAddress,
                    5555
                );

                StatusMessage = $"Connected to {SelectedPeer.HostName}";
            }
            catch (SocketException)
            {
                StatusMessage = $"Connection refused — {SelectedPeer.HostName} is not hosting";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Connection failed: {ex.Message}";
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
                Messages.Add(msg);
            });
        };
    }

    // =====================
    // HELPERS
    // =====================

    private void LoadPeers()
    {
        var peers = _db.Peers
            .OrderByDescending(p => p.LastSeen)
            .ToList();

        AvailablePeers.Clear();
        foreach (var peer in peers)
            AvailablePeers.Add(peer);
    }

    private async Task LoadHistoryAsync(PeerNode peer)
    {
        var history = await _db.Messages
            .Where(m => m.PeerId == peer.PeerId)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Clear();

            foreach (var msg in history)
            {
                Messages.Add(new ChatMessage(
                    msg.Direction == MessageDirection.Outgoing ? "local" : peer.ProtocolPeerId,
                    peer.ProtocolPeerId,
                    msg.Content
                ));
            }
        });
    }

}
