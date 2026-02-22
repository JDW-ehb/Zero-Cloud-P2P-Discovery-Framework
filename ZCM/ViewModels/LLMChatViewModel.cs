using Microsoft.Maui.Dispatching;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Repositories.IA;
using ZCL.Services.LLM;

namespace ZCM.ViewModels;

public sealed class LLMMessage
{
    public string Content { get; set; } = string.Empty;
    public bool IsUser { get; set; }
}

public sealed class LLMChatViewModel : BindableObject
{
    private readonly ZcspPeer _peer;
    private readonly LLMChatService _llm;
    private readonly ILLMChatRepository _repo;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isThinking;

    private PeerNode? _activePeer;
    private LLMConversationItem? _activeConversation;
    private LLMPeerItem? _selectedPeer;
    private LLMConversationItem? _selectedConversation;
    private Guid _activeSessionId = Guid.Empty;

    public ObservableCollection<LLMMessage> Messages { get; } = new();
    public ObservableCollection<LLMConversationItem> Conversations { get; } = new();
    public ObservableCollection<LLMPeerItem> AvailablePeers { get; } = new();

    public ICommand StartNewConversationCommand { get; }
    public ICommand SendCommand { get; }

    public LLMPeerItem? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (_selectedPeer == value)
                return;

            _selectedPeer = value;
            OnPropertyChanged();

            if (value != null)
                _ = LoadConversationsForPeerAsync(value.Peer.PeerId);
        }
    }

    public LLMConversationItem? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (_selectedConversation == value)
                return;

            _selectedConversation = value;
            OnPropertyChanged();

            if (value != null)
                _ = ActivateConversationAsync(value);
        }
    }

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

    public LLMChatViewModel(
        ZcspPeer peer,
        LLMChatService llm,
        ILLMChatRepository repo)
    {
        _peer = peer;
        _llm = llm;
        _repo = repo;
        _llm.SessionStarted += (sessionId, remotePeerId) =>
        {
            if (_activePeer?.ProtocolPeerId == remotePeerId)
                _activeSessionId = sessionId;
        };
        _llm.ResponseReceived += OnResponseAsync;

        StartNewConversationCommand =
            new Command(async () => await StartNewConversationAsync());

        SendCommand = new Command(
            async () => await SendAsync(),
            () => IsConnected && !string.IsNullOrWhiteSpace(Prompt));
    }

    public async Task InitializeAsync()
    {
        await LoadPeersAsync();
    }

    private async Task LoadPeersAsync()
    {
        var peers = await _repo.GetAvailablePeersAsync();

        AvailablePeers.Clear();

        foreach (var (peer, model) in peers)
        {
            AvailablePeers.Add(new LLMPeerItem
            {
                Peer = peer,
                Model = model
            });
        }
    }

    private async Task LoadConversationsForPeerAsync(Guid peerId)
    {
        var conversations = await _repo.GetConversationsForPeerAsync(peerId);

        Conversations.Clear();

        foreach (var c in conversations)
        {
            Conversations.Add(new LLMConversationItem
            {
                Id = c.Id,
                PeerId = c.PeerId,
                PeerName = _selectedPeer?.Peer.HostName ?? "Unknown",
                Model = c.Model,
                Summary = c.Summary
            });
        }
    }

    private async Task StartNewConversationAsync()
    {
        if (SelectedPeer == null)
        {
            Status = "Select a peer first.";
            return;
        }

        var conversationId = await _repo.CreateConversationAsync(
            SelectedPeer.Peer.PeerId,
            SelectedPeer.Model ?? "unknown");

        var convo = new LLMConversationItem
        {
            Id = conversationId,
            PeerId = SelectedPeer.Peer.PeerId,
            PeerName = SelectedPeer.Peer.HostName,
            Model = SelectedPeer.Model ?? "unknown"
        };

        Conversations.Insert(0, convo);
        SelectedConversation = convo;
    }

    private async Task ActivateConversationAsync(LLMConversationItem convo)
    {
        await _lock.WaitAsync();

        try
        {
            _activeConversation = convo;

            var result = await _repo
                .GetLlmServiceForPeerAsync(convo.PeerId, convo.Model);

            if (result == null)
            {
                Status = "LLM service unavailable on this peer.";
                IsConnected = false;
                return;
            }

            var (peer, service) = result.Value;
            _activePeer = peer;

            Status = "Connecting…";
            IsConnected = false;

            _activeSessionId = Guid.Empty;

            await _peer.ConnectAsync(
                service.Address,
                service.Port,
                peer.ProtocolPeerId,
                _llm);

            if (_activeSessionId == Guid.Empty)
            {
                Status = "Connected, but session not initialized.";
                IsConnected = false;
                return;
            }

            IsConnected = true;
            Status = $"Connected ({service.Metadata})";

            await LoadHistoryAsync(convo.Id);
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

    private async Task LoadHistoryAsync(Guid conversationId)
    {
        var history = await _repo.GetHistoryAsync(conversationId);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Clear();

            foreach (var msg in history)
            {
                Messages.Add(new LLMMessage
                {
                    Content = msg.Content,
                    IsUser = msg.IsUser
                });
            }
        });
    }

    private async Task SendAsync()
    {
        if (!IsConnected || _activeConversation == null || _isThinking)
            return;

        var text = Prompt.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        _isThinking = true;
        Prompt = string.Empty;

        Messages.Add(new LLMMessage
        {
            Content = text,
            IsUser = true
        });

        await _repo.StoreAsync(_activeConversation.Id, text, true);

        if (string.IsNullOrWhiteSpace(_activeConversation.Summary))
        {
            var title = text.Length > 40
                ? text[..40]
                : text;

            title = title.Replace("\n", " ").Trim();

            _activeConversation.Summary = title;
            await _repo.UpdateSummaryAsync(_activeConversation.Id, title);
        }

        try
        {
            Status = "Thinking…";
            if (_activeSessionId == Guid.Empty)
                throw new InvalidOperationException("No active LLM session.");

            await _llm.SendQueryAsync(_activeSessionId, text);
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

    private async Task OnResponseAsync(string response)
    {
        if (_activeConversation == null)
            return;

        var clean = response.Trim();

        await _repo.StoreAsync(_activeConversation.Id, clean, false);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add(new LLMMessage
            {
                Content = clean,
                IsUser = false
            });

            Status = "Connected";
        });
    }
}
