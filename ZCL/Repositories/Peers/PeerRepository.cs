using Microsoft.EntityFrameworkCore;
using ZCL.Models;

namespace ZCL.Repositories.Peers;

public sealed class PeerRepository : IPeerRepository
{
    private readonly ServiceDBContext _db;

    // Per-instance lock (not static). Helps if the same repo instance is hit concurrently.
    // If everything stays on one thread per DbContext (ideal), you could remove this entirely.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PeerRepository(ServiceDBContext db)
    {
        _db = db;
    }

    public async Task<PeerNode> GetOrCreateAsync(
        string protocolPeerId,
        string? ipAddress = null,
        string? hostName = null,
        bool isLocal = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(protocolPeerId))
            throw new ArgumentException(nameof(protocolPeerId));

        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            var peer = await _db.Peers
                .FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId, ct);

            if (peer != null)
            {
                peer.LastSeen = now;

                if (!string.IsNullOrWhiteSpace(ipAddress))
                    peer.IpAddress = ipAddress;

                if (!string.IsNullOrWhiteSpace(hostName))
                    peer.HostName = hostName;

                // If caller says it's local, upgrade it (but be careful: local uniqueness is enforced elsewhere)
                if (isLocal && !peer.IsLocal)
                {
                    peer.IsLocal = true;
                    peer.OnlineStatus = PeerOnlineStatus.Online;
                }

                await _db.SaveChangesAsync(ct);
                return peer;
            }

            peer = new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = protocolPeerId,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress!,
                HostName = string.IsNullOrWhiteSpace(hostName) ? protocolPeerId : hostName!,
                FirstSeen = now,
                LastSeen = now,
                OnlineStatus = isLocal ? PeerOnlineStatus.Online : PeerOnlineStatus.Unknown,
                IsLocal = isLocal
            };

            _db.Peers.Add(peer);
            await _db.SaveChangesAsync(ct);

            return peer;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<PeerNode?> GetByProtocolIdAsync(string protocolPeerId, CancellationToken ct = default)
    {
        return _db.Peers.FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId, ct);
    }

    public Task<PeerNode> GetLocalPeerAsync(CancellationToken ct = default)
    {
        // If duplicates exist, this throws. That’s OK if DB constraint exists.
        // Otherwise use OrderByDescending(...).FirstAsync(...) similar to GetLocalPeerIdAsync.
        return _db.Peers.SingleAsync(p => p.IsLocal, ct);
    }

    public Task<List<PeerNode>> GetAllAsync(CancellationToken ct = default)
    {
        return _db.Peers
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync(ct);
    }

    public async Task EnsureLocalPeerAsync(string localProtocolPeerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localProtocolPeerId))
            throw new ArgumentException(nameof(localProtocolPeerId));

        await _lock.WaitAsync(ct);
        try
        {
            var locals = await _db.Peers
                .Where(p => p.IsLocal)
                .OrderByDescending(p => p.LastSeen)
                .ToListAsync(ct);

            if (locals.Count == 0)
            {
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

                await _db.SaveChangesAsync(ct);
                return;
            }

            // Keep newest local, demote extras (self-heal)
            var keep = locals[0];

            if (keep.ProtocolPeerId != localProtocolPeerId)
            {
                keep.ProtocolPeerId = localProtocolPeerId;
                keep.HostName = localProtocolPeerId;
            }

            foreach (var extra in locals.Skip(1))
                extra.IsLocal = false;

            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<Guid?> GetLocalPeerIdAsync(CancellationToken ct = default)
    {
        return _db.Peers
            .Where(p => p.IsLocal)
            .OrderByDescending(p => p.LastSeen)
            .Select(p => (Guid?)p.PeerId)
            .FirstOrDefaultAsync(ct);
    }
}
