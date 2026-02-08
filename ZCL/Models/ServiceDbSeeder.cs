using ZCL.Repositories.Peers;

public static class ServiceDbSeeder
{
    public static async Task SeedAsync(IPeerRepository peers)
    {
        // Seed only REMOTE peers
        await peers.GetOrCreateAsync("peer-alpha", "192.168.1.10", "Alpha");
        await peers.GetOrCreateAsync("peer-beta", "192.168.1.48", "Beta");
        await peers.GetOrCreateAsync("peer-gamma", "192.168.1.42", "Gamma");
    }
}
