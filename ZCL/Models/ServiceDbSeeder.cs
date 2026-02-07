using ZCL.Repositories.Peers;

public static class ServiceDbSeeder
{
    public static async Task SeedAsync(
    IPeerRepository peers,
    string localProtocolPeerId)
    {
        await peers.GetOrCreateAsync(
            protocolPeerId: localProtocolPeerId,
            ipAddress: "127.0.0.1",
            hostName: localProtocolPeerId,
            isLocal: true);

        await peers.GetOrCreateAsync("peer-alpha", "192.168.1.10", "Alpha");
        await peers.GetOrCreateAsync("peer-beta", "192.168.1.48", "Beta");
        await peers.GetOrCreateAsync("peer-gamma", "192.168.1.42", "Gamma");
    }

}
