using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
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
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isThinking;

    private PeerNode? _activePeer;
    private AiPeerItem? _selectedPeer;

    public ObservableCollection<AiMessage> Messages { get; } = new();

    private string _prompt = string.Empty;
    public string Prompt
    {
        get => _prompt;
        set { _prompt = value; OnPropertyChanged(); ((Command)SendCommand).ChangeCanExecute(); }
    }
    public ObservableCollection<AiPeerItem> AiPeers { get; } = new();


    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); }
    }

    private string _status = "Idle";
    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public ICommand SendCommand { get; }

    public AiChatViewModel(ZcspPeer peer, AiChatService ai)
    {
        _peer = peer;
        _ai = ai;

        _ai.ResponseReceived += OnResponse;

        SendCommand = new Command(
            async () => await SendAsync(),
            () => IsConnected && !string.IsNullOrWhiteSpace(Prompt));
    }

    public async Task ActivatePeerAsync(PeerNode peer)
    {
        await _lock.WaitAsync();
        try
        {
            _activePeer = peer;

            Status = "Connecting…";
            IsConnected = false;
            ((Command)SendCommand).ChangeCanExecute();

            // connect to ZCSP host on peer
            await _peer.ConnectAsync(peer.IpAddress, 5555, peer.ProtocolPeerId, _ai);

            IsConnected = true;
            Status = "Connected";
            ((Command)SendCommand).ChangeCanExecute();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Clear();
                Messages.Add(new AiMessage
                {
                    Content = $"Connected to {peer.HostName} ({peer.IpAddress})",
                    IsUser = false
                });

            });
        }
        catch
        {
            IsConnected = false;
            Status = "Offline";
            ((Command)SendCommand).ChangeCanExecute();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SendAsync()
    {
        if (!IsConnected || _activePeer == null || _isThinking)
            return;

        _isThinking = true;

        var text = Prompt.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        Prompt = string.Empty;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add(new AiMessage
            {
                Content = text,
                IsUser = true
            });
        });

        try
        {
            Status = "Thinking…";
            await _ai.SendQueryAsync(text);
        }
        catch
        {
            IsConnected = false;
            Status = "Connection lost";
            ((Command)SendCommand).ChangeCanExecute();
        }
        finally
        {
            _isThinking = false;
        }
    }


    private void OnResponse(string response)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add(new AiMessage
            {
                Content = response.Trim(),
                IsUser = false
            });
            Status = "Connected";
        });
    }

    private async Task LoadAiPeersAsync()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var peers = await db.Services
            .Where(s => s.Name == "AIChat")
            .Include(s => s.Peer)
            .ToListAsync();

        foreach (var s in peers)
        {
            AiPeers.Add(new AiPeerItem
            {
                Peer = s.Peer!,
                Model = s.Metadata
            });
        }
    }

    public async Task InitializeAsync()
    {
        await LoadAiPeersAsync();

        if (AiPeers.Count > 0)
            await SelectPeerAsync(AiPeers[0]);
    }
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


}
