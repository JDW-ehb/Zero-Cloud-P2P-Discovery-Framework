using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ZCL.APIs.ZCSP.Protocol;
using ZCL.APIs.ZCSP.Sessions;
using ZCL.APIs.ZCSP.Transport;

namespace ZCL.APIs.ZCSP
{
    public sealed class ZcspPeer
    {
        private readonly string _peerId;
        private readonly SessionRegistry _sessions;
        private readonly Dictionary<string, IZcspService> _services;

        public ZcspPeer(
            string peerId,
            SessionRegistry sessions,
            IEnumerable<IZcspService> services)
        {
            _peerId = peerId;
            _sessions = sessions;
            _services = new Dictionary<string, IZcspService>();

            foreach (var service in services)
                _services[service.ServiceName] = service;
        }

        // =====================
        // HOSTING
        // =====================

        public async Task StartHostingAsync(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{_peerId}] Hosting on port {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var frame = await Framing.ReadAsync(stream);
                if (frame == null) return;

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceRequest) return;

                reader.ReadBytes(16); // requestId
                var fromPeer = BinaryCodec.ReadString(reader);
                BinaryCodec.ReadString(reader); // toPeer (ignored)
                var serviceName = BinaryCodec.ReadString(reader);

                if (!_services.TryGetValue(serviceName, out var service))
                    return;

                var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));

                var response = BinaryCodec.Serialize(
                    ZcspMessageType.ServiceResponse,
                    session.Id,
                    w =>
                    {
                        w.Write(true);
                        w.Write(session.ExpiresAt.Ticks);
                    });

                await Framing.WriteAsync(stream, response);

                service.BindStream(stream);
                await service.OnSessionStartedAsync(session.Id, fromPeer);

                await RunSessionAsync(stream, session.Id, service);
            }
        }

        // =====================
        // CONNECTING CLIENT -> HOST
        // =====================

        public async Task ConnectAsync(string host, int port, string serviceName)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            using var stream = client.GetStream();

            var request = BinaryCodec.Serialize(
                ZcspMessageType.ServiceRequest,
                null,
                w =>
                {
                    w.Write(Guid.NewGuid().ToByteArray());
                    BinaryCodec.WriteString(w, _peerId);
                    BinaryCodec.WriteString(w, "remote");
                    BinaryCodec.WriteString(w, serviceName);
                });

            await Framing.WriteAsync(stream, request);

            var frame = await Framing.ReadAsync(stream);
            if (frame == null) return;

            var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);
            if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                return;

            var service = _services[serviceName];
            service.BindStream(stream);

            await service.OnSessionStartedAsync(sessionId.Value, host);
            await RunSessionAsync(stream, sessionId.Value, service);
        }

        // =====================
        // SESSION LOOP
        // =====================

        private async Task RunSessionAsync(
            NetworkStream stream,
            Guid sessionId,
            IZcspService service)
        {
            while (true)
            {
                var frame = await Framing.ReadAsync(stream);
                if (frame == null) break;

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);

                if (type == ZcspMessageType.SessionClose)
                    break;

                if (type == ZcspMessageType.SessionData)
                    await service.OnSessionDataAsync(sessionId, reader);
            }

            await service.OnSessionClosedAsync(sessionId);
            _sessions.Remove(sessionId);
        }
    }
}
