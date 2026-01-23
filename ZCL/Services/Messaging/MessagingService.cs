using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ZCL.APIs.ZCSP;
using ZCL.APIs.ZCSP.Protocol;

namespace ZCL.Services.Messaging
{
    public sealed class MessagingService : IZcspService
    {
        public string ServiceName => "Messaging";

        private readonly ConcurrentDictionary<string, List<ChatMessage>> _messages = new();

        public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
        {
            // Nothing special needed for messaging (yet)
            return Task.CompletedTask;
        }

        public Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
        {
            // Payload format owned by MessagingService
            var fromPeer = BinaryCodec.ReadString(reader);
            var toPeer = BinaryCodec.ReadString(reader);
            var content = BinaryCodec.ReadString(reader);

            Store(fromPeer, toPeer, content);
            return Task.CompletedTask;
        }

        public Task OnSessionClosedAsync(Guid sessionId)
        {
            return Task.CompletedTask;
        }

        // =====================
        // Messaging logic
        // =====================

        public ChatMessage Store(string fromPeer, string toPeer, string content)
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

        public IReadOnlyList<ChatMessage> GetConversation(string peerA, string peerB)
        {
            var key = BuildConversationKey(peerA, peerB);

            return _messages.TryGetValue(key, out var conversation)
                ? conversation.AsReadOnly()
                : Array.Empty<ChatMessage>();
        }

        private static string BuildConversationKey(string a, string b)
        {
            return string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";
        }
    }
}
