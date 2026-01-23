using System;
using System.Threading.Tasks;
using ZCL.APIs.ZCSP;
using ZCL.APIs.ZCSP.Sessions;
using ZCL.Services.Messaging;

class Program
{
    static async Task Main()
    {
        var peer = BuildPeer("peer-A");

        // Start hosting in background
        _ = peer.StartHostingAsync(5555);

        // Give listener a moment to bind
        await Task.Delay(500);

        // Uncomment in second console to connect
        // await peer.ConnectAsync("127.0.0.1", 5555, "Messaging");

        await Task.Delay(Timeout.Infinite);
    }

    static ZcspPeer BuildPeer(string peerId)
    {
        var sessions = new SessionRegistry();

        // Instantiate services
        var messagingService = new MessagingService();

        // Register services with ZCSP
        IZcspService[] services =
        {
            messagingService
            // FileTransferService later
            // AIChatService later
        };

        return new ZcspPeer(peerId, sessions, services);
    }
}
