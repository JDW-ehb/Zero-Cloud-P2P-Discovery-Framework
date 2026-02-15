using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Repositories.IA;
using ZCL.Services.AI;

namespace ZCM.ViewModels;

public sealed class AiMessage
{
    public string Content { get; set; } = "";
    public bool IsUser { get; set; }
}

public sealed class AiChatViewModel : BindableObject
{
    private readonly ZcspPeer _peer;
    private readonly AiChatService _ai;
    private readonly IAiChatRepository _repo;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isThinking;

    private PeerNode? _activePeer;
    private AiPeerItem? _selectedPeer;

    public ObservableCollection<AiMessage> Messages { get; } = new();
    public ObservableCollection<AiPeerItem> AiPeers { get; } = new();

    private string _prompt = string.Empty;
    public string Prompt
    {
        get => _prompt;
        set
        {
            _prompt = value;
            OnPropertyChanged();
            ((Command)SendCommand).ChangeCanExecute();
        }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            _isConnected = value;
            OnPropertyChanged();
            ((Command)SendCommand).ChangeCanExecute();
        }
    }

    private string _status = "Idle";
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public ICommand SendCommand { get; }

    public AiChatViewModel(
        ZcspPeer peer,
        AiChatService ai,
        IAiChatRepository repo)
    {
        _peer = peer;
        _ai = ai;
        _repo = repo;

        _ai.ResponseReceived += OnResponse;

        SendCommand = new Command(
            async () => await SendAsync(),
            () => IsConnected && !string.IsNullOrWhiteSpace(Prompt));
    }

    // ===============================
    // INITIALIZATION
    // ===============================

    public async Task InitializeAsync()
    {
        await LoadAiPeersAsync();

        if (AiPeers.Count > 0)
            await SelectPeerAsync(AiPeers[0]);
    }

    private async Task LoadAiPeersAsync()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var services = await db.Services
            .Where(s => s.Name == "AIChat")
            .Include(s => s.Peer)
            .ToListAsync();

        AiPeers.Clear();

        foreach (var s in services)
        {
            AiPeers.Add(new AiPeerItem
            {
                Peer = s.Peer!,
                Model = s.Metadata
            });
        }
    }

    // ===============================
    // PEER SELECTION
    // ===============================

    public AiPeerItem? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (_selectedPeer == value)
                return;

            _selectedPeer = value;
            OnPropertyChanged();

            if (value != null)
                _ = SelectPeerAsync(value);
        }
    }

    private async Task SelectPeerAsync(AiPeerItem item)
    {
        await ActivatePeerAsync(item.Peer);
    }

    public async Task ActivatePeerAsync(PeerNode peer)
    {
        await _lock.WaitAsync();
        try
        {
            _activePeer = peer;

            Status = "Connecting…";
            IsConnected = false;

            await _peer.ConnectAsync(
                peer.IpAddress,
                5555,
                peer.ProtocolPeerId,
                _ai);

            IsConnected = true;
            Status = "Connected";

            await LoadHistoryAsync(peer);
        }
        catch
        {
            Status = "Offline";
            IsConnected = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ===============================
    // HISTORY LOADING
    // ===============================

    private async Task LoadHistoryAsync(PeerNode peer)
    {
        var history = await _repo.GetHistoryAsync(peer.PeerId);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Clear();

            foreach (var msg in history)
            {
                Messages.Add(new AiMessage
                {
                    Content = msg.Content,
                    IsUser = msg.IsUser
                });
            }
        });
    }

    // ===============================
    // SENDING
    // ===============================

    private async Task SendAsync()
    {
        if (!IsConnected || _activePeer == null || _selectedPeer == null || _isThinking)
            return;

        var text = Prompt.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        _isThinking = true;
        Prompt = string.Empty;

        Messages.Add(new AiMessage
        {
            Content = text,
            IsUser = true
        });

        await _repo.StoreAsync(
            _activePeer.PeerId,
            _selectedPeer.Model,
            text,
            isUser: true);

        try
        {
            Status = "Thinking…";
            await _ai.SendQueryAsync(text);
        }
        catch
        {
            Status = "Connection lost";
            IsConnected = false;
        }
        finally
        {
            _isThinking = false;
        }
    }

    // ===============================
    // RECEIVING
    // ===============================

    private async void OnResponse(string response)
    {
        if (_activePeer == null || _selectedPeer == null)
            return;

        var clean = response.Trim();

        await _repo.StoreAsync(
            _activePeer.PeerId,
            _selectedPeer.Model,
            clean,
            isUser: false);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add(new AiMessage
            {
                Content = clean,
                IsUser = false
            });

            Status = "Connected";
        });
    }
}
