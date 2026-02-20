using System.Net.Sockets;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Protocol.ZCSP
{
    public sealed class SessionBroker
    {
        private readonly ZcspPeer _peer;
        private readonly ConnectivityManager _connectivity;

        private NetworkStream? _serverStream;
        private Guid _serverSessionId;

        private readonly string _serverProtocolPeerId;

        public SessionBroker(
            ZcspPeer peer,
            ConnectivityManager connectivity,
            string serverProtocolPeerId)
        {
            _peer = peer;
            _connectivity = connectivity;
            _serverProtocolPeerId = serverProtocolPeerId;
        }

        public async Task SendAsync(
            string targetProtocolPeerId,
            string targetIp,
            int port,
            IZcspService service,
            byte[] innerPayload,
            Guid sessionId)
        {
            if (_connectivity.Mode == ConnectivityMode.Routed)
            {
                await EnsureServerSessionAsync(service);

                var routeId = Guid.NewGuid();

                var frame = BinaryCodec.Serialize(
                    ZcspMessageType.RoutedSessionData,
                    sessionId,
                    w =>
                    {
                        RoutingEnvelope.Write(
                            w,
                            routeId,
                            _peer.PeerId,
                            targetProtocolPeerId,
                            service.ServiceName,
                            innerPayload);
                    });

                await Framing.WriteAsync(_serverStream!, frame);
            }
            else
            {
                await _peer.ConnectAsync(targetIp, port, targetProtocolPeerId, service);

                var direct = BinaryCodec.Serialize(
                    ZcspMessageType.SessionData,
                    sessionId,
                    w => w.Write(innerPayload));

                await Framing.WriteAsync(serviceStream(service), direct);
            }
        }

        private async Task EnsureServerSessionAsync(IZcspService service)
        {
            if (_serverStream != null)
                return;

            await _peer.ConnectAsync(
                host: "SERVER_IP_HERE",
                port: 5555,
                remotePeerId: _serverProtocolPeerId,
                service: service);

            // You can capture session id via OnSessionStarted callback if needed.
        }

        private NetworkStream serviceStream(IZcspService service)
        {
            var field = service.GetType()
                .GetField("_stream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            return (NetworkStream)field!.GetValue(service)!;
        }
    }
}
