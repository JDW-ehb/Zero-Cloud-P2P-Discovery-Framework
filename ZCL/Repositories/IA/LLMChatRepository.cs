using System;
using System.Collections.Generic;
using System.Text;
using ZCL.Models;
using Microsoft.EntityFrameworkCore;

namespace ZCL.Repositories.IA
{
    public sealed class LLMChatRepository : ILLMChatRepository
    {
        private readonly ServiceDBContext _db;

        public LLMChatRepository(ServiceDBContext db)
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

            _db.LLMConversations.Add(convo);
            await _db.SaveChangesAsync();

            return convo.Id;
        }

        public async Task StoreAsync(Guid conversationId, string content, bool isUser)
        {
            _db.LLMMessages.Add(new LLMMessageEntity
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
            return _db.LLMMessages
                .Where(x => x.ConversationId == conversationId)
                .OrderBy(x => x.Timestamp)
                .ToListAsync();
        }
        public async Task UpdateSummaryAsync(Guid conversationId, string summary)
        {
            var convo = await _db.LLMConversations
                .FirstOrDefaultAsync(c => c.Id == conversationId);

            if (convo == null)
                return;

            convo.Summary = summary;
            await _db.SaveChangesAsync();
        }

        public Task<List<LLMConversationEntity>> GetConversationsForPeerAsync(Guid peerId)
        {
            return _db.LLMConversations
                .Where(c => c.PeerId == peerId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
        public async Task<List<(PeerNode Peer, string Model)>> GetAvailablePeersAsync()
        {
            var services = await _db.Services
                .Where(s => s.Name == "LLMChat")
                .Include(s => s.Peer)
                .ToListAsync();

            return services
                .Where(s => s.Peer != null)
                .Select(s => (s.Peer!, s.Metadata ?? "unknown"))
                .ToList();
        }

        public async Task<(PeerNode Peer, Service Service)?> GetLlmServiceForPeerAsync(Guid peerId, string model)
        {
            var service = await _db.Services
                .Include(s => s.Peer)
                .FirstOrDefaultAsync(s =>
                    s.PeerRefId == peerId &&
                    s.Name == "LLMChat" &&
                    s.Metadata == model);

            if (service?.Peer == null)
                return null;

            return (service.Peer, service);
        }

    }


}
