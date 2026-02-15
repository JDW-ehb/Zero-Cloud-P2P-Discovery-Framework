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
    private AiConversationItem? _activeConversation;

    public ObservableCollection<AiMessage> Messages { get; } = new();
    public ObservableCollection<AiConversationItem> Conversations { get; } = new();

    public ICommand StartNewConversationCommand { get; }
    public ICommand SendCommand { get; }

    private AiConversationItem? _selectedConversation;
    public AiConversationItem? SelectedConversation
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

    public AiChatViewModel(
        ZcspPeer peer,
        AiChatService ai,
        IAiChatRepository repo)
    {
        _peer = peer;
        _ai = ai;
        _repo = repo;

        _ai.ResponseReceived += OnResponse;

        StartNewConversationCommand = new Command(async () => await StartNewConversationAsync());

        SendCommand = new Command(
            async () => await SendAsync(),
            () => IsConnected && !string.IsNullOrWhiteSpace(Prompt));
    }

    // =========================================
    // INITIALIZATION
    // =========================================

    public async Task InitializeAsync()
    {
        await LoadConversationsAsync();
    }

    private async Task LoadConversationsAsync()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var conversations = await db.AiConversations
            .Include(c => c.Peer)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        Conversations.Clear();

        foreach (var c in conversations)
        {
            Conversations.Add(new AiConversationItem
            {
                Id = c.Id,
                PeerId = c.PeerId,
                PeerName = c.Peer?.HostName ?? "Unknown",
                Model = c.Model
            });
        }
    }

    // =========================================
    // NEW CONVERSATION
    // =========================================

    private async Task StartNewConversationAsync()
    {
        var peers = await GetAvailablePeersAsync();

        if (peers.Count == 0)
        {
            Status = "No AI peers available.";
            return;
        }

        var options = peers
            .Select(p => $"{p.Peer.HostName} ({p.Model})")
            .ToArray();

        var selected = await Application.Current!.MainPage!
            .DisplayActionSheetAsync("Select AI Peer", "Cancel", null, options);

        if (selected == null || selected == "Cancel")
            return;

        var chosen = peers.First(p =>
            $"{p.Peer.HostName} ({p.Model})" == selected);

        var conversationId = await _repo.CreateConversationAsync(
            chosen.Peer.PeerId,
            chosen.Model);

        var convo = new AiConversationItem
        {
            Id = conversationId,
            PeerId = chosen.Peer.PeerId,
            PeerName = chosen.Peer.HostName,
            Model = chosen.Model
        };

        Conversations.Insert(0, convo);
        SelectedConversation = convo;
    }

    // =========================================
    // ACTIVATE CONVERSATION
    // =========================================

    private async Task ActivateConversationAsync(AiConversationItem convo)
    {
        await _lock.WaitAsync();

        try
        {
            _activeConversation = convo;

            var peer = await GetPeerByIdAsync(convo.PeerId);
            if (peer == null)
                return;

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
                Messages.Add(new AiMessage
                {
                    Content = msg.Content,
                    IsUser = msg.IsUser
                });
            }
        });
    }

    // =========================================
    // SEND
    // =========================================

    private async Task SendAsync()
    {
        if (!IsConnected || _activeConversation == null || _isThinking)
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
            _activeConversation.Id,
            text,
            true);

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

    // =========================================
    // RECEIVE
    // =========================================

    private async void OnResponse(string response)
    {
        if (_activeConversation == null)
            return;

        var clean = response.Trim();

        await _repo.StoreAsync(
            _activeConversation.Id,
            clean,
            false);

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

    // =========================================
    // HELPERS
    // =========================================

    private async Task<PeerNode?> GetPeerByIdAsync(Guid peerId)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
        return await db.PeerNodes.FirstOrDefaultAsync(p => p.PeerId == peerId);
    }

    private async Task<List<(PeerNode Peer, string Model)>> GetAvailablePeersAsync()
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var services = await db.Services
            .Where(s => s.Name == "AIChat")
            .Include(s => s.Peer)
            .ToListAsync();

        return services
            .Where(s => s.Peer != null)
            .Select(s => (s.Peer!, s.Metadata ?? "unknown"))
            .ToList();
    }
}