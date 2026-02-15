using System;
using System.Collections.Generic;
using System.Text;

namespace ZCL.Repositories.IA
{
    public interface IAiChatRepository
    {
        Task StoreAsync(Guid peerId, string model, string content, bool isUser);
        Task<List<AiMessageEntity>> GetHistoryAsync(Guid peerId);
    }

}
