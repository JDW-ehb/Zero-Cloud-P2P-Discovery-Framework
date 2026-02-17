using Microsoft.EntityFrameworkCore;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Repositories.IA;
using ZCL.Services.AI;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class AiChatPage : ContentPage
{
    private readonly AiChatViewModel _vm;

    public AiChatPage(PeerNode? preselectPeer = null)
    {
        InitializeComponent();

        _vm = new AiChatViewModel(
            ServiceHelper.GetService<ZcspPeer>(),
            ServiceHelper.GetService<AiChatService>(),
            ServiceHelper.GetService<IAiChatRepository>());

        BindingContext = _vm;

        Loaded += async (_, __) =>
        {
            await InitializeAsync(preselectPeer);
        };
    }

    private async Task InitializeAsync(PeerNode? preselectPeer)
    {
        await _vm.InitializeAsync();

        if (preselectPeer == null)
            return;

        // Try to find existing conversation for this peer
        var convo = _vm.Conversations
            .FirstOrDefault(c => c.PeerId == preselectPeer.PeerId);

        if (convo != null)
        {
            _vm.SelectedConversation = convo;
        }
        else
        {
            using var scope = ServiceHelper.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

            var service = await db.Services
                .FirstOrDefaultAsync(s =>
                    s.PeerRefId == preselectPeer.PeerId &&
                    s.Name == "AIChat");

            var model = service?.Metadata ?? "unknown";

            _vm.Conversations.Add(new AiConversationItem
            {
                PeerId = preselectPeer.PeerId,
                PeerName = preselectPeer.HostName,
                Model = model
            });


            _vm.SelectedConversation = _vm.Conversations.Last();
        }
    }
}
