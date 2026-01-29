using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
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
    // UI state
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

    public ObservableCollection<PeerNode> AvailablePeers { get; } = new();

    private PeerNode? _selectedPeer;
    public PeerNode? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            _selectedPeer = value;
            OnPropertyChanged();
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
    // Commands
    // =====================

    public ICommand HostCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand SendMessageCommand { get; }

    // =====================
    // Constructor
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
            if (!IsHosting)
                return;

            await _peer.StartHostingAsync(
                5555,
                name => name == _messaging.ServiceName ? _messaging : null
            );
        });

        ConnectCommand = new Command(async () =>
        {
            if (SelectedPeer == null)
                return;

            await _messaging.ConnectToPeerAsync(
                SelectedPeer.IpAddress,
                5555
            );
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
    // Helpers
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
}
