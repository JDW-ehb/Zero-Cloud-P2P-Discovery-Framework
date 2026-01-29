using ZCL.Models;

public static class ServiceDbSeeder
{
    public static void Seed(ServiceDBContext db)
    {
        // Create DB + tables if missing
        db.Database.EnsureCreated();

        // Prevent duplicate seeding
        if (db.Peers.Any())
            return;

        var now = DateTime.UtcNow;

        db.Peers.AddRange(
            new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = "peer-alpha",
                IpAddress = "192.168.1.10",
                HostName = "Alpha",
                FirstSeen = now,
                LastSeen = now,
                OnlineStatus = PeerOnlineStatus.Online
            },
            new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = "peer-beta",
                IpAddress = "192.168.1.22",
                HostName = "Beta",
                FirstSeen = now,
                LastSeen = now,
                OnlineStatus = PeerOnlineStatus.Online
            },
            new PeerNode
            {
                PeerId = Guid.NewGuid(),
                ProtocolPeerId = "peer-gamma",
                IpAddress = "192.168.1.42",
                HostName = "Gamma",
                FirstSeen = now,
                LastSeen = now,
                OnlineStatus = PeerOnlineStatus.Offline
            }
        );

        db.SaveChanges();
    }
}
