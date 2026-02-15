using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.Dispatching;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Services.AI;

namespace ZCM.ViewModels;

public sealed class AiChatViewModel : BindableObject
{
    private readonly ZcspPeer _peer;
    private readonly AiChatService _ai;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private PeerNode? _activePeer;

    public ObservableCollection<string> Messages { get; } = new();

    private string _prompt = string.Empty;
    public string Prompt
    {
        get => _prompt;
        set { _prompt = value; OnPropertyChanged(); ((Command)SendCommand).ChangeCanExecute(); }
    }

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
                Messages.Add($"[system] Connected to {peer.HostName} ({peer.IpAddress})");
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
        if (!IsConnected || _activePeer == null)
            return;

        var text = Prompt.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        Prompt = string.Empty;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add($"> {text}");
        });

        try
        {
            await _ai.SendQueryAsync(text);
            Status = "Thinking…";
        }
        catch
        {
            IsConnected = false;
            Status = "Connection lost";
            ((Command)SendCommand).ChangeCanExecute();
        }
    }

    private void OnResponse(string response)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add(response.Trim());
            Status = "Connected";
        });
    }
}
