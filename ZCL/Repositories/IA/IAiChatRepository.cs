using System;
using System.Collections.Generic;
using System.Text;

namespace ZCL.Repositories.IA
{
    public interface IAiChatRepository
    {
        Task<Guid> CreateConversationAsync(Guid peerId, string model);
        Task StoreAsync(Guid conversationId, string content, bool isUser);
        Task<List<AiMessageEntity>> GetHistoryAsync(Guid conversationId);
    }
}
