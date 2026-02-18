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
            var convo = new LLMConversationEntity
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
            _db.AiMessages.Add(new LLMMessageEntity
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Content = content,
                IsUser = isUser,
                Timestamp = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
        }

        public Task<List<LLMMessageEntity>> GetHistoryAsync(Guid conversationId)
        {
            return _db.AiMessages
                .Where(x => x.ConversationId == conversationId)
                .OrderBy(x => x.Timestamp)
                .ToListAsync();
        }
        public async Task UpdateSummaryAsync(Guid conversationId, string summary)
        {
            var convo = await _db.AiConversations
                .FirstOrDefaultAsync(c => c.Id == conversationId);

            if (convo == null)
                return;

            convo.Summary = summary;
            await _db.SaveChangesAsync();
        }

    }


}
