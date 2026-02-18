using ZCL.Models;

namespace ZCL.Repositories.IA
{
    public interface ILLMChatRepository
    {
        Task<Guid> CreateConversationAsync(Guid peerId, string model);
        Task StoreAsync(Guid conversationId, string content, bool isUser);
        Task<List<LLMMessageEntity>> GetHistoryAsync(Guid conversationId);
        Task UpdateSummaryAsync(Guid conversationId, string summary);

        Task<List<LLMConversationEntity>> GetConversationsForPeerAsync(Guid peerId);
        Task<List<(PeerNode Peer, string Model)>> GetAvailablePeersAsync();
        Task<(PeerNode Peer, Service Service)?> GetLlmServiceForPeerAsync(Guid peerId, string model);
    }
}
