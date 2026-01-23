using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using ZCL.APIs.ZCSP;
using ZCL.APIs.ZCSP.Protocol;
using ZCL.APIs.ZCSP.Transport;

namespace ZCL.Services.Messaging
{
    public sealed class MessagingService : IZcspService
    {
        public string ServiceName => "Messaging";

        private NetworkStream? _stream;
        private readonly ConcurrentDictionary<string, List<ChatMessage>> _messages = new();

        public void BindStream(NetworkStream stream)
        {
            _stream = stream;
        }

        public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
        {
            Console.WriteLine($"[Messaging] Session started with {remotePeerId}");
            Console.WriteLine("Type messages and press Enter\n");

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var line = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(line) || _stream == null)
                        continue;

                    var data = BinaryCodec.Serialize(
                        ZcspMessageType.SessionData,
                        sessionId,
                        w =>
                        {
                            BinaryCodec.WriteString(w, "local");
                            BinaryCodec.WriteString(w, remotePeerId);
                            BinaryCodec.WriteString(w, line);
                        });

                    await Framing.WriteAsync(_stream, data);
                }
            });

            return Task.CompletedTask;
        }

        public Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
        {
            var fromPeer = BinaryCodec.ReadString(reader);
            var toPeer = BinaryCodec.ReadString(reader);
            var content = BinaryCodec.ReadString(reader);

            var msg = Store(fromPeer, toPeer, content);
            Console.WriteLine(msg);

            return Task.CompletedTask;
        }

        public Task OnSessionClosedAsync(Guid sessionId)
        {
            Console.WriteLine("[Messaging] Session closed");
            return Task.CompletedTask;
        }

        // =====================
        // Messaging logic
        // =====================

        private ChatMessage Store(string fromPeer, string toPeer, string content)
        {
            var message = new ChatMessage(fromPeer, toPeer, content);
            var key = BuildConversationKey(fromPeer, toPeer);

            var conversation = _messages.GetOrAdd(key, _ => new List<ChatMessage>());
            lock (conversation)
            {
                conversation.Add(message);
            }

            return message;
        }

        private static string BuildConversationKey(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";
    }
}
