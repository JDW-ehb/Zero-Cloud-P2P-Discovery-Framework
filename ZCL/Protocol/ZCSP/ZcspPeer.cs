using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Protocol.ZCSP
{
    public sealed class ZcspPeer
    {
        private readonly string _peerId;
        private readonly SessionRegistry _sessions;
        public string PeerId => _peerId;
        public ZcspPeer(string peerId, SessionRegistry sessions)
        {
            _peerId = peerId;
            _sessions = sessions;
        }

        // =====================
        // HOSTING (SERVER SIDE)
        // =====================

        public async Task StartHostingAsync(
            int port,
            Func<string, IZcspService?> serviceResolver)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{_peerId}] Hosting on port {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client, serviceResolver));
            }
        }

        private async Task HandleClientAsync(
            TcpClient client,
            Func<string, IZcspService?> serviceResolver)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // ---- Expect ServiceRequest ----
                var frame = await Framing.ReadAsync(stream);
                if (frame == null)
                    return;

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceRequest)
                    return;

                // ---- Parse request ----
                reader.ReadBytes(16); // requestId
                var fromPeer = BinaryCodec.ReadString(reader);
                BinaryCodec.ReadString(reader); // toPeer (ignored)
                var serviceName = BinaryCodec.ReadString(reader);

                // ---- Resolve service externally ----
                var service = serviceResolver(serviceName);
                if (service == null)
                    return;

                // ---- Create session ----
                var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));

                // ---- Respond ----
                var response = BinaryCodec.Serialize(
                    ZcspMessageType.ServiceResponse,
                    session.Id,
                    w =>
                    {
                        w.Write(true);
                        w.Write(session.ExpiresAt.Ticks);
                    });

                await Framing.WriteAsync(stream, response);

                // ---- Activate service ----
                service.BindStream(stream);
                await service.OnSessionStartedAsync(session.Id, fromPeer);

                // ---- Run session ----
                await RunSessionAsync(stream, session.Id, service);
            }
        }

        // =====================
        // CONNECTING (CLIENT SIDE)
        // =====================

        public async Task ConnectAsync(
            string host,
            int port,
            string remotePeerId,
            IZcspService service)
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port);
            var stream = client.GetStream();

            // ---- Send ServiceRequest ----
            var request = BinaryCodec.Serialize(
                ZcspMessageType.ServiceRequest,
                null,
                w =>
                {
                    w.Write(Guid.NewGuid().ToByteArray());
                    BinaryCodec.WriteString(w, _peerId);
                    BinaryCodec.WriteString(w, remotePeerId);
                    BinaryCodec.WriteString(w, service.ServiceName);
                });

            await Framing.WriteAsync(stream, request);

            // ---- Await ServiceResponse ----
            var frame = await Framing.ReadAsync(stream);
            if (frame == null)
                return;

            var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);
            if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                return;

            service.BindStream(stream);
            // was: await service.OnSessionStartedAsync(sessionId.Value, host);
            await service.OnSessionStartedAsync(sessionId.Value, remotePeerId);

            // 🔥 session loop owns the connection now
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSessionAsync(stream, sessionId.Value, service);
                }
                finally
                {
                    stream.Dispose();
                    client.Dispose();
                }
            });
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
                if (frame == null)
                    break;

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
