using Microsoft.EntityFrameworkCore;
using ZCL.Models;

namespace ZCL.Repositories.Peers;

public sealed class PeerRepository : IPeerRepository
{
    private readonly ServiceDBContext _db;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public PeerRepository(ServiceDBContext db)
    {
        _db = db;
    }

    public async Task<PeerNode> GetOrCreateAsync(
    string protocolPeerId,
    string? ipAddress,
    string? hostName,
    bool isLocal = false)
    {
        await _lock.WaitAsync();
        try
        {
            var peer = await _db.Peers
                .FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId);

            if (peer != null)
            {
                peer.LastSeen = DateTime.UtcNow;

                if (ipAddress != null)
                    peer.IpAddress = ipAddress;

                if (hostName != null)
                    peer.HostName = hostName;

                await _db.SaveChangesAsync();
                return peer;
            }

            peer = new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = protocolPeerId,
                IpAddress = ipAddress ?? "unknown",
                HostName = hostName ?? protocolPeerId,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                OnlineStatus = isLocal
                    ? PeerOnlineStatus.Online
                    : PeerOnlineStatus.Unknown,
                IsLocal = isLocal
            };

            _db.Peers.Add(peer);
            await _db.SaveChangesAsync();

            return peer;
        }
        finally
        {
            _lock.Release();
        }
    }


    public async Task<PeerNode> GetOrCreateAsync(string protocolPeerId)
    {
        await _lock.WaitAsync();
        try
        {
            var peer = await _db.Peers
                .FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId);

            if (peer != null)
            {
                peer.LastSeen = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return peer;
            }

            peer = new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = protocolPeerId,
                IpAddress = "unknown",
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                OnlineStatus = PeerOnlineStatus.Unknown,
                IsLocal = false
            };

            _db.Peers.Add(peer);
            await _db.SaveChangesAsync();

            return peer;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PeerNode?> GetByProtocolIdAsync(string protocolPeerId)
    {
        return await _db.Peers
            .FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId);
    }

    public async Task<PeerNode> GetLocalPeerAsync()
    {
        return await _db.Peers
            .SingleAsync(p => p.IsLocal);
    }

    public async Task<List<PeerNode>> GetAllAsync()
    {
        return await _db.Peers
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync();
    }

    public async Task EnsureLocalPeerAsync(string localProtocolPeerId)
    {
        if (await _db.Peers.AnyAsync(p => p.IsLocal))
            return;

        var now = DateTime.UtcNow;

        _db.Peers.Add(new PeerNode
        {
            PeerId = Guid.NewGuid(),
            ProtocolPeerId = localProtocolPeerId,
            HostName = localProtocolPeerId,
            IpAddress = "127.0.0.1",
            FirstSeen = now,
            LastSeen = now,
            OnlineStatus = PeerOnlineStatus.Online,
            IsLocal = true
        });

        await _db.SaveChangesAsync();
    }
}
