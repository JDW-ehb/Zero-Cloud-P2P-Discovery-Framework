using System;
using System.Threading.Tasks;
using ZCL.APIs.ZCSP;
using ZCL.APIs.ZCSP.Sessions;
using ZCL.Services.Messaging;

class Program
{
    static async Task Main()
    {
        // Create protocol engine
        var sessions = new SessionRegistry();
        var peer = new ZcspPeer("peer-A", sessions);

        // Create service that USES the protocol
        var messaging = new MessagingService(peer);

        // Start hosting (server capability)
        _ = peer.StartHostingAsync(
            port: 5555,
            serviceResolver: name =>
                name == messaging.ServiceName ? messaging : null
        );

        // Small delay so listener is ready
        await Task.Delay(500);

        // Initiate connection THROUGH the service
        await messaging.ConnectToPeerAsync("127.0.0.1", 5555);

        await Task.Delay(Timeout.Infinite);
    }
}
