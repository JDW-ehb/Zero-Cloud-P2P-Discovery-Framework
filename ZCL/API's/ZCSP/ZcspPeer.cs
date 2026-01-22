using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ZCL.APIs.ZCSP.Protocol;
using ZCL.APIs.ZCSP.Sessions;
using ZCL.APIs.ZCSP.Transport;
using ZCL.Services.Messaging;

namespace ZCL.APIs.ZCSP
{
    public sealed class ZcspPeer
    {
        private readonly string _peerId;
        private readonly SessionRegistry _sessions;
        private readonly MessagingService _messaging;

        public ZcspPeer(
            string peerId,
            SessionRegistry sessions,
            MessagingService messaging)
        {
            _peerId = peerId;
            _sessions = sessions;
            _messaging = messaging;
        }

        // =====================
        // HOSTING (server role)
        // =====================

        public async Task StartHostingAsync(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{_peerId}] Hosting on port {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var stream = client.GetStream();

            while (true)
            {
                var frame = await Framing.ReadAsync(stream);
                if (frame == null) return;

                var (type, sessionId, _, reader) =
                    BinaryCodec.Deserialize(frame);

                switch (type)
                {
                    case ZcspMessageType.ServiceRequest:
                        await HandleServiceRequestAsync(stream, reader);
                        break;

                    case ZcspMessageType.SessionData:
                        await HandleSessionDataAsync(stream, sessionId, reader);
                        break;

                    case ZcspMessageType.SessionClose:
                        if (sessionId.HasValue)
                            _sessions.Remove(sessionId.Value);
                        return;
                }
            }
        }

        private async Task HandleServiceRequestAsync(
            NetworkStream stream,
            BinaryReader reader)
        {
            reader.ReadBytes(16); // requestId
            var fromPeer = BinaryCodec.ReadString(reader);
            BinaryCodec.ReadString(reader); // toPeer
            var service = BinaryCodec.ReadString(reader);

            if (service != "Messaging")
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

            Console.WriteLine($"[{_peerId}] Session opened: {session.Id}");
        }

        private async Task HandleSessionDataAsync(
            NetworkStream stream,
            Guid? sessionId,
            BinaryReader reader)
        {
            if (sessionId == null)
                return;

            if (!_sessions.TryGet(sessionId.Value, out var session))
                return;

            var incomingText = BinaryCodec.ReadString(reader);

            var message = _messaging.Store(
                fromPeer: session.PeerId,
                toPeer: _peerId,
                content: incomingText);

            Console.WriteLine(message);

            var response = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                session.Id,
                w => BinaryCodec.WriteString(w, message.Content));

            await Framing.WriteAsync(stream, response);
        }

        // =====================
        // CONNECTING (client role)
        // =====================

        public async Task ConnectAsync(string host, int port)
        {
            using var client = new TcpClient();
            Console.WriteLine($"[{_peerId}] Connecting to {host}:{port}");
            await client.ConnectAsync(host, port);

            using var stream = client.GetStream();

            var request = BinaryCodec.Serialize(
                ZcspMessageType.ServiceRequest,
                null,
                w =>
                {
                    w.Write(Guid.NewGuid().ToByteArray()); // requestId
                    BinaryCodec.WriteString(w, _peerId);
                    BinaryCodec.WriteString(w, "remote");
                    BinaryCodec.WriteString(w, "Messaging");
                });

            await Framing.WriteAsync(stream, request);

            var frame = await Framing.ReadAsync(stream);
            if (frame == null) return;

            var (type, sessionId, _, reader) =
                BinaryCodec.Deserialize(frame);

            if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                return;

            var approved = reader.ReadBoolean();
            reader.ReadInt64(); // expiresAt

            if (!approved)
            {
                Console.WriteLine("Service request rejected");
                return;
            }

            Console.WriteLine($"[{_peerId}] Session established: {sessionId}");

            await StartChatAsync(stream, sessionId.Value);
        }

        // =====================
        // SHARED CHAT LOOP
        // =====================

        private async Task StartChatAsync(NetworkStream stream, Guid sessionId)
        {
            Console.WriteLine("Chat started. Press Ctrl+C to exit.\n");

            var receive = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await Framing.ReadAsync(stream);
                    if (frame == null) break;

                    var (type, sessionId, timestamp, reader) =
                        BinaryCodec.Deserialize(frame);

                    Console.WriteLine($"\n[REMOTE] {BinaryCodec.ReadString(reader)}");
                }
            });

            var send = Task.Run(async () =>
            {
                while (true)
                {
                    var line = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var data = BinaryCodec.Serialize(
                        ZcspMessageType.SessionData,
                        sessionId,
                        w => BinaryCodec.WriteString(w, line));

                    await Framing.WriteAsync(stream, data);
                }
            });

            await Task.WhenAny(send, receive);
        }
    }
}
