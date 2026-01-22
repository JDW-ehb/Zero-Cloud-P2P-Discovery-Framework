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

        // Connect to another peer
        await peer.ConnectAsync("127.0.0.1", 5556);

        // Keep process alive
        await Task.Delay(Timeout.Infinite);
    }
    static ZcspPeer BuildPeer(string peerId)
    {
        var sessions = new SessionRegistry();
        var messaging = new MessagingService();

        return new ZcspPeer(peerId, sessions, messaging);
    }
}
