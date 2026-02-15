using System;
using System.Collections.Generic;
using System.Text;
using ZCL.Models;
using Microsoft.EntityFrameworkCore;

namespace ZCL.Repositories.IA
{
    public sealed class AiChatRepository : IAiChatRepository
    {
        private readonly ServiceDBContext _db;

        public AiChatRepository(ServiceDBContext db)
        {
            _db = db;
        }

        public async Task<Guid> CreateConversationAsync(Guid peerId, string model)
        {
            var convo = new AiConversationEntity
            {
                Id = Guid.NewGuid(),
                PeerId = peerId,
                Model = model,
                CreatedAt = DateTime.UtcNow
            };

            _db.AiConversations.Add(convo);
            await _db.SaveChangesAsync();

            return convo.Id;
        }

        public async Task StoreAsync(Guid conversationId, string content, bool isUser)
        {
            _db.AiMessages.Add(new AiMessageEntity
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Content = content,
                IsUser = isUser,
                Timestamp = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
        }

        public Task<List<AiMessageEntity>> GetHistoryAsync(Guid conversationId)
        {
            return _db.AiMessages
                .Where(x => x.ConversationId == conversationId)
                .OrderBy(x => x.Timestamp)
                .ToListAsync();
        }
    }


}
