using Microsoft.EntityFrameworkCore;
using ZCL.Models;

namespace ZCL.Repositories.Peers;

public sealed class PeerRepository : IPeerRepository
{
    private readonly ServiceDBContext _db;


    private readonly SemaphoreSlim _lock = new(1, 1);

    public PeerRepository(ServiceDBContext db)
    {
        _db = db;
    }

    public async Task<string> GetOrCreateLocalProtocolPeerIdAsync(
    string hostName,
    string ipAddress = "127.0.0.1",
    CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            var local = await _db.PeerNodes
                .Where(p => p.IsLocal)
                .OrderByDescending(p => p.LastSeen)
                .FirstOrDefaultAsync(ct);

            if (local != null)
            {
                // One-time legacy repair only
                if (!Guid.TryParse(local.ProtocolPeerId, out _))
                    local.ProtocolPeerId = Guid.NewGuid().ToString();

                local.HostName = string.IsNullOrWhiteSpace(hostName)
                    ? local.HostName
                    : hostName;

                local.IpAddress = string.IsNullOrWhiteSpace(ipAddress)
                    ? local.IpAddress
                    : ipAddress;

                local.LastSeen = now;
                local.OnlineStatus = PeerOnlineStatus.Online;

                await _db.SaveChangesAsync(ct);
                return local.ProtocolPeerId;
            }

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

            try
            {
                await _db.SaveChangesAsync(ct);
                return protocolId;
            }
            catch (DbUpdateException ex) when
            (
                ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19
            )
            {
                var existing = await _db.PeerNodes
                    .Where(p => p.IsLocal)
                    .OrderByDescending(p => p.LastSeen)
                    .FirstAsync(ct);

                return existing.ProtocolPeerId;
            }
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

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when
            (
                ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19 
            )
            {

                peer = await _db.PeerNodes
                    .FirstAsync(p => p.ProtocolPeerId == protocolPeerId, ct);
            }

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
        return _db.PeerNodes.SingleAsync(p => p.IsLocal, ct);
    }

    public Task<List<PeerNode>> GetAllAsync(CancellationToken ct = default)
    {
        return _db.PeerNodes
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync(ct);
    }

    public Task<Guid?> GetLocalPeerIdAsync(CancellationToken ct = default)
    {
        return _db.PeerNodes
            .Where(p => p.IsLocal)
            .OrderByDescending(p => p.LastSeen)
            .Select(p => (Guid?)p.PeerId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PeerNode?> GetByProtocolPeerIdAsync(
    string protocolPeerId,
    CancellationToken ct = default)
    {
        return await _db.PeerNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.ProtocolPeerId == protocolPeerId,
                ct);
    }
    public Task<PeerNode?> GetByIdAsync(Guid peerId)
    {
        return _db.PeerNodes.FirstOrDefaultAsync(p => p.PeerId == peerId);
    }

    public async Task<string?> GetLocalProtocolPeerIdAsync(CancellationToken ct = default)
    {
        var local = await _db.PeerNodes
            .Where(p => p.IsLocal)
            .OrderByDescending(p => p.LastSeen)
            .FirstOrDefaultAsync(ct);

        return local?.ProtocolPeerId;
    }
}
