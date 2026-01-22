using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ZCL.Services.Messaging
{
    public sealed class MessagingService
    {
        // conversationKey → ordered messages
        private readonly ConcurrentDictionary<string, List<ChatMessage>> _messages
            = new();

        /// <summary>
        /// Stores an incoming message and returns it.
        /// </summary>
        public ChatMessage Store(string fromPeer, string toPeer, string content)
        {
            var message = new ChatMessage(fromPeer, toPeer, content);

            // Deterministic conversation key (A|B == B|A)
            var conversationKey = BuildConversationKey(fromPeer, toPeer);

            var conversation = _messages.GetOrAdd(
                conversationKey,
                _ => new List<ChatMessage>());

            lock (conversation)
            {
                conversation.Add(message);
            }

            return message;
        }

        public IReadOnlyList<ChatMessage> GetConversation(string peerA, string peerB)
        {
            var key = BuildConversationKey(peerA, peerB);

            if (_messages.TryGetValue(key, out var conversation))
                return conversation.AsReadOnly();

            return Array.Empty<ChatMessage>();
        }

        private static string BuildConversationKey(string a, string b)
        {
            return string.CompareOrdinal(a, b) < 0
                ? $"{a}|{b}"
                : $"{b}|{a}";
        }
    }
}
