using Microsoft.Extensions.DependencyInjection;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.Peers;

var services = new ServiceCollection();

// ===============================
// Register dependencies
// ===============================
services.AddDbContext<ServiceDBContext>();
services.AddScoped<IPeerRepository, PeerRepository>();
services.AddSingleton<SessionRegistry>();
services.AddSingleton<ZcspPeer>();

var provider = services.BuildServiceProvider();

// ===============================
// Enable coordinator mode
// ===============================
Config.Instance.IsCoordinator = true;

Console.WriteLine("Zero-Cloud Coordinator starting...");
Console.WriteLine($"Machine: {Environment.MachineName}");
Console.WriteLine("Coordinator mode ENABLED");

// ===============================
// Start hosting (routing mode)
// ===============================
var zcspPeer = provider.GetRequiredService<ZcspPeer>();

await zcspPeer.StartRoutingHostAsync(
    port: 5555,
    serviceResolver: serviceName =>
    {
        // Coordinator does not host real services.
        // It only forwards routed sessions.
        return null;
    });