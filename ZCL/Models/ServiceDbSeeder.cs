using ZCL.Models;

public static class ServiceDbSeeder
{
    public static void Seed(ServiceDBContext db, string localProtocolPeerId)
    {
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;

        // =====================
        // Ensure LOCAL peer
        // =====================
        if (!db.Peers.Any(p => p.IsLocal))
        {
            db.Peers.Add(new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = localProtocolPeerId,
                HostName = localProtocolPeerId,
                IpAddress = "127.0.0.1",      // no static IP
                FirstSeen = now,
                LastSeen = now,
                OnlineStatus = PeerOnlineStatus.Online,
                IsLocal = true
            });
        }

        // =====================
        // Ensure DEMO peers
        // =====================
        EnsurePeer(db, now, "peer-alpha", "Alpha", "192.168.1.10", PeerOnlineStatus.Online);
        EnsurePeer(db, now, "peer-beta", "Beta", "192.168.1.48", PeerOnlineStatus.Online);
        EnsurePeer(db, now, "peer-gamma", "Gamma", "192.168.1.42", PeerOnlineStatus.Offline);

        db.SaveChanges();
    }

    private static void EnsurePeer(
        ServiceDBContext db,
        DateTime now,
        string protocolPeerId,
        string hostName,
        string ipAddress,
        PeerOnlineStatus status)
    {
        if (db.Peers.Any(p => p.ProtocolPeerId == protocolPeerId))
            return;

        db.Peers.Add(new PeerNode
        {
            PeerId = Guid.NewGuid(),
            ProtocolPeerId = protocolPeerId,
            HostName = hostName,
            IpAddress = ipAddress,
            FirstSeen = now,
            LastSeen = now,
            OnlineStatus = status,
            IsLocal = false
        });
    }
}
