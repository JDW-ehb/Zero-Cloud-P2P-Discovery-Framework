using System.Threading.Tasks;
using ZCL.APIs.ZCSP;
using ZCL.APIs.ZCSP.Sessions;
using ZCL.Services.Messaging;

class Program
{
    static async Task Main()
    {
        // ---- compose dependencies ----
        var sessions = new SessionRegistry();
        var messaging = new MessagingService();

        var peer = new ZcspPeer(
            peerId: "peer-A",
            sessions: sessions,
            messaging: messaging
        );

        // ---- start listening ----
        await peer.StartHostingAsync(5555);
    }
}
