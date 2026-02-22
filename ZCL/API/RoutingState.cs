using ZCL.Models;

namespace ZCL.API
{
    public sealed class RoutingState
    {
        private readonly object _lock = new();

        public RoutingMode Mode { get; private set; } = RoutingMode.Direct;

        public string? ServerHost { get; private set; }
        public int ServerPort { get; private set; }
        public string? ServerProtocolPeerId { get; private set; }

        public NodeRole Role { get; private set; }

        public void SetDirect()
        {
            lock (_lock)
            {
                Mode = RoutingMode.Direct;
                ServerHost = null;
                ServerPort = 0;
                ServerProtocolPeerId = null;
            }
        }

        public void SetServer(string host, int port, string protocolPeerId)
        {
            lock (_lock)
            {
                Mode = RoutingMode.ViaServer;
                ServerHost = host;
                ServerPort = port;
                ServerProtocolPeerId = protocolPeerId;
            }
        }

        public void Initialize(NodeRole role)
        {
            Role = role;
        }


    }
}