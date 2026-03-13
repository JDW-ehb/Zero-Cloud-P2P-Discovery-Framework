using Microsoft.EntityFrameworkCore;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Repositories.IA;
using ZCL.Services.LLM;
using ZCM.Controls;
using ZCM.ViewModels;

namespace ZCM.Pages;

[QueryProperty(nameof(Peer), "Peer")]
public partial class LLMChatPage : ContentPage, IDrawerPage
{
    public DrawerHost? PageDrawer => Drawer;

    private readonly LLMChatViewModel _vm;

    private PeerNode? _preselectPeer;

    public LLMChatPage()
    {
        InitializeComponent();

        Sidebar.Host = Drawer;

        _vm = new LLMChatViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<LLMChatService>(),
            ServiceHelper.GetService<ILLMChatRepository>());

        BindingContext = _vm;

        Loaded += async (_, __) =>
        {
            await _vm.InitializeAsync();

            if (_preselectPeer != null)
                await ActivatePeerAsync(_preselectPeer);
        };
    }

    public PeerNode? Peer
    {
        get => _preselectPeer;
        set => _preselectPeer = value;
    }

    private async Task ActivatePeerAsync(PeerNode preselectPeer)
    {
        var convo = _vm.Conversations
            .FirstOrDefault(c => c.PeerId == preselectPeer.PeerId);

        if (convo != null)
        {
            _vm.SelectedConversation = convo;
            return;
        }

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var service = await db.Services
            .FirstOrDefaultAsync(s =>
                s.PeerRefId == preselectPeer.PeerId &&
                s.Name == "LLMChat");

        var model = service?.Metadata ?? "unknown";

        _vm.Conversations.Add(new LLMConversationItem
        {
            PeerId = preselectPeer.PeerId,
            PeerName = preselectPeer.HostName,
            Model = model
        });

        _vm.SelectedConversation = _vm.Conversations.Last();
    }
}