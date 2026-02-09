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

    /// <summary>
    /// DB-backed persistent local ProtocolPeerId.
    /// - If no local peer exists: create one with a new GUID ProtocolPeerId and return it.
    /// - If local peer exists: ensure ProtocolPeerId is a GUID string (fix legacy values like "Luca's desktop"),
    ///   update hostname/ip, self-heal duplicates, and return the kept ProtocolPeerId.
    /// </summary>
    public async Task<string> GetOrCreateLocalProtocolPeerIdAsync(
        string hostName,
        string ipAddress = "127.0.0.1",
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            var locals = await _db.PeerNodes
                .Where(p => p.IsLocal)
                .OrderByDescending(p => p.LastSeen)
                .ToListAsync(ct);

            if (locals.Count == 0)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE PeerNodes SET IsLocal = 0 WHERE IsLocal = 1",
                    ct);

                _db.ChangeTracker.Clear();


                var protocolId = Guid.NewGuid().ToString();

                _db.PeerNodes.Add(new PeerNode
                {
                    PeerId = Guid.NewGuid(),
                    ProtocolPeerId = protocolId,
                    HostName = string.IsNullOrWhiteSpace(hostName) ? protocolId : hostName,
                    IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "127.0.0.1" : ipAddress,
                    FirstSeen = now,
                    LastSeen = now,
                    OnlineStatus = PeerOnlineStatus.Online,
                    IsLocal = true
                });

                await _db.SaveChangesAsync(ct);
                return protocolId;
            }


            // Keep newest local, demote extras (self-heal)
            var keep = locals[0];

            // Legacy fix: if local ProtocolPeerId is not a GUID, replace it
            if (!Guid.TryParse(keep.ProtocolPeerId, out _))
                keep.ProtocolPeerId = Guid.NewGuid().ToString();

            keep.HostName = string.IsNullOrWhiteSpace(hostName) ? keep.HostName : hostName;
            keep.IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? keep.IpAddress : ipAddress;
            keep.LastSeen = now;
            keep.OnlineStatus = PeerOnlineStatus.Online;

            foreach (var extra in locals.Skip(1))
                extra.IsLocal = false;

            await _db.SaveChangesAsync(ct);
            return keep.ProtocolPeerId;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PeerNode> GetOrCreateAsync(
        string protocolPeerId,
        string? ipAddress = null,
        string? hostName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(protocolPeerId))
            throw new ArgumentException(nameof(protocolPeerId));

        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            var peer = await _db.PeerNodes
                .FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId, ct);

            if (peer != null)
            {
                peer.LastSeen = now;

                if (!string.IsNullOrWhiteSpace(ipAddress))
                    peer.IpAddress = ipAddress;

                if (!string.IsNullOrWhiteSpace(hostName))
                    peer.HostName = hostName;

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
                OnlineStatus = PeerOnlineStatus.Unknown,
                IsLocal = false
            };

            _db.PeerNodes.Add(peer);
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
        return _db.PeerNodes.FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId, ct);
    }

    public Task<PeerNode> GetLocalPeerAsync(CancellationToken ct = default)
    {
        // If duplicates exist, this throws. That’s OK if DB constraint exists.
        // Otherwise use OrderByDescending(...).FirstAsync(...) similar to GetLocalPeerIdAsync.
        return _db.PeerNodes.SingleAsync(p => p.IsLocal, ct);
    }

    public Task<List<PeerNode>> GetAllAsync(CancellationToken ct = default)
    {
        return _db.PeerNodes
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Keeps newest local row, demotes extras. Also ensures the kept row matches the provided localProtocolPeerId.
    /// Note: if you move fully to GetOrCreateLocalProtocolPeerIdAsync, this method becomes mostly redundant.
    /// </summary>
    public async Task EnsureLocalPeerAsync(string localProtocolPeerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localProtocolPeerId))
            throw new ArgumentException(nameof(localProtocolPeerId));

        await _lock.WaitAsync(ct);
        try
        {
            var locals = await _db.PeerNodes
                .Where(p => p.IsLocal)
                .OrderByDescending(p => p.LastSeen)
                .ToListAsync(ct);

            if (locals.Count == 0)
            {
                var now = DateTime.UtcNow;

                _db.PeerNodes.Add(new PeerNode
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
        return _db.PeerNodes
            .Where(p => p.IsLocal)
            .OrderByDescending(p => p.LastSeen)
            .Select(p => (Guid?)p.PeerId)
            .FirstOrDefaultAsync(ct);
    }
}
