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

        public async Task StoreAsync(Guid peerId, string model, string content, bool isUser)
        {
            _db.AiMessages.Add(new AiMessageEntity
            {
                Id = Guid.NewGuid(),
                PeerId = peerId,
                Model = model,
                Content = content,
                IsUser = isUser,
                Timestamp = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
        }

        public Task<List<AiMessageEntity>> GetHistoryAsync(Guid peerId)
        {
            return _db.AiMessages
                .Where(x => x.PeerId == peerId)
                .OrderBy(x => x.Timestamp)
                .ToListAsync();
        }
    }

}
