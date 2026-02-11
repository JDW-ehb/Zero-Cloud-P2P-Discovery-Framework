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
        // NOTE: DbSet is PeerNodes (not Peers) after replacing Peer -> PeerNode
        return _db.PeerNodes
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync();
    }


    public async Task<List<PeerNode>> GetPeersWithMessagesAsync()
    {
        var localId = await GetLocalPeerIdAsync();

        if (localId == null)
            return new List<PeerNode>();

        var remotePeerIds = await _db.Messages
            .Where(m => m.FromPeerId == localId || m.ToPeerId == localId)
            .Select(m => m.FromPeerId == localId ? m.ToPeerId : m.FromPeerId)
            .Distinct()
            .ToListAsync();

        return await _db.PeerNodes
            .Where(p => remotePeerIds.Contains(p.PeerId))
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync();
    }


    public async Task<Guid?> GetLocalPeerIdAsync()
    {
        return await _db.PeerNodes
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

    public Task<MessageEntity?> GetLastMessageBetweenAsync(
        Guid localPeerId,
        Guid remotePeerDbId,
        CancellationToken ct = default)
        => _db.Messages
            .Where(m =>
                (m.FromPeerId == localPeerId && m.ToPeerId == remotePeerDbId) ||
                (m.FromPeerId == remotePeerDbId && m.ToPeerId == localPeerId))
            .OrderByDescending(m => m.Timestamp)
            .FirstOrDefaultAsync(ct);
}
