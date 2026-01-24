using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ZCL
{
    public enum ZcspMessageType : byte
    {
        ServiceRequest = 1,
        ServiceResponse = 2,
        SessionData = 3,
        SessionClose = 4
    }

    // =====================
    // TCP FRAMING
    // =====================

    static class Framing
    {
        public static async Task WriteAsync(NetworkStream stream, byte[] payload)
        {
            var len = BitConverter.GetBytes(payload.Length);
            await stream.WriteAsync(len);
            await stream.WriteAsync(payload);
        }

        public static async Task<byte[]?> ReadAsync(NetworkStream stream)
        {
            var lenBuf = new byte[4];
            if (await ReadExact(stream, lenBuf) == 0)
                return null;

            int len = BitConverter.ToInt32(lenBuf);
            var payload = new byte[len];
            await ReadExact(stream, payload);
            return payload;
        }

        private static async Task<int> ReadExact(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset));
                if (read == 0)
                    return 0;
                offset += read;
            }
            return offset;
        }
    }

    // =====================
    // BINARY CODEC
    // =====================

    static class BinaryCodec
    {
        public static void WriteString(BinaryWriter w, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        public static string ReadString(BinaryReader r)
        {
            int len = r.ReadInt32();
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        public static byte[] Serialize(
            ZcspMessageType type,
            Guid? sessionId,
            Action<BinaryWriter> payload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write((byte)type);
            w.Write(sessionId.HasValue);
            if (sessionId.HasValue)
                w.Write(sessionId.Value.ToByteArray());
            w.Write(DateTime.UtcNow.Ticks);

            payload(w);
            return ms.ToArray();
        }

        public static (ZcspMessageType type, Guid? sessionId, BinaryReader reader)
            Deserialize(byte[] data)
        {
            var r = new BinaryReader(new MemoryStream(data));

            var type = (ZcspMessageType)r.ReadByte();
            Guid? sessionId = null;

            if (r.ReadBoolean())
                sessionId = new Guid(r.ReadBytes(16));

            r.ReadInt64(); // timestamp (not used here)

            return (type, sessionId, r);
        }
    }

    // =====================
    // PEER
    // =====================

    public class ZcspPeer
    {
        private readonly string _peerId;
        private readonly Dictionary<Guid, bool> _sessions = new();

        public ZcspPeer(string peerId)
        {
            _peerId = peerId;
        }

        // =====================
        // SERVER ROLE (accepts connections)
        // =====================

        public async Task StartServerAsync(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{_peerId}] Listening on port {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var stream = client.GetStream();

            // Wait for ServiceRequest
            var frame = await Framing.ReadAsync(stream);
            if (frame == null) return;

            var (type, _, reader) = BinaryCodec.Deserialize(frame);
            if (type != ZcspMessageType.ServiceRequest) return;

            // Read request
            reader.ReadBytes(16); // requestId
            BinaryCodec.ReadString(reader); // from
            BinaryCodec.ReadString(reader); // to
            BinaryCodec.ReadString(reader); // service

            var sessionId = Guid.NewGuid();
            _sessions[sessionId] = true;

            // Send ServiceResponse
            var response = BinaryCodec.Serialize(
                ZcspMessageType.ServiceResponse,
                sessionId,
                w =>
                {
                    w.Write(true);
                    w.Write(DateTime.UtcNow.AddMinutes(30).Ticks);
                });

            await Framing.WriteAsync(stream, response);
            Console.WriteLine($"[{_peerId}] Session created: {sessionId}");

            // ENTER INTERACTIVE CHAT
            await StartChatAsync(stream, sessionId);
        }

        // =====================
        // CLIENT ROLE (connects)
        // =====================

        public async Task RunClientAsync(string host, int port)
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
                    w.Write(Guid.NewGuid().ToByteArray());
                    BinaryCodec.WriteString(w, _peerId);
                    BinaryCodec.WriteString(w, "peer");
                    BinaryCodec.WriteString(w, "Messaging");
                });

            await Framing.WriteAsync(stream, request);

            var frame = await Framing.ReadAsync(stream);
            var (_, sessionId, _) = BinaryCodec.Deserialize(frame!);

            Console.WriteLine($"[{_peerId}] Session established: {sessionId}");

            // ENTER INTERACTIVE CHAT
            await StartChatAsync(stream, sessionId!.Value);
        }

        // =====================
        // INTERACTIVE CHAT LOOP (BOTH SIDES)
        // =====================

        private async Task StartChatAsync(NetworkStream stream, Guid sessionId)
        {
            Console.WriteLine("Chat started. Press ESC to quit.\n");

            var receive = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await Framing.ReadAsync(stream);
                    if (frame == null) break;

                    var (type, _, r) = BinaryCodec.Deserialize(frame);

                    if (type == ZcspMessageType.SessionData)
                        Console.WriteLine($"\n[REMOTE] {BinaryCodec.ReadString(r)}");
                    else if (type == ZcspMessageType.SessionClose)
                        break;
                }
            });

            var send = Task.Run(async () =>
            {
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        var close = BinaryCodec.Serialize(
                            ZcspMessageType.SessionClose,
                            sessionId,
                            _ => { });

                        await Framing.WriteAsync(stream, close);
                        break;
                    }

                    Console.Write(key.KeyChar);
                    var msg = key.KeyChar + Console.ReadLine();

                    var data = BinaryCodec.Serialize(
                        ZcspMessageType.SessionData,
                        sessionId,
                        w => BinaryCodec.WriteString(w, msg));

                    await Framing.WriteAsync(stream, data);
                }
            });

            await Task.WhenAny(send, receive);
        }
    }
}