using Microsoft.EntityFrameworkCore;
using ZCL.Models;

namespace ZCL.Services.Messaging;

public sealed class ChatQueryService : IChatQueryService
{
    private readonly ServiceDBContext _db;

    public ChatQueryService(ServiceDBContext db)
    {
        _db = db;
    }

    public Task<List<PeerNode>> GetPeersAsync()
    {
        return _db.Peers
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync();
    }

    public async Task<Guid?> GetLocalPeerIdAsync()
    {
        // Safe even if there are multiple "local" rows (shouldn't happen, but you've seen it)
        return await _db.Peers
            .Where(p => p.IsLocal)
            .OrderByDescending(p => p.LastSeen)
            .Select(p => (Guid?)p.PeerId)
            .FirstOrDefaultAsync();
    }

    public Task<List<MessageEntity>> GetHistoryAsync(Guid localPeerId, Guid remotePeerId)
    {
        return _db.Messages
            .Where(m =>
                (m.FromPeerId == localPeerId && m.ToPeerId == remotePeerId) ||
                (m.FromPeerId == remotePeerId && m.ToPeerId == localPeerId))
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }
}
