using System;
using ZCL.Models;

namespace ZCL.Services.Messaging
    {
        public sealed class ChatMessage
        {
            public Guid Id { get; }
            public string FromPeer { get; }
            public string ToPeer { get; }
            public string Content { get; }
            public DateTime Timestamp { get; }

            public MessageDirection Direction { get; }

            public bool IsOutgoing => Direction == MessageDirection.Outgoing;
            public bool IsIncoming => Direction == MessageDirection.Incoming;

            public ChatMessage(
                string fromPeer,
                string toPeer,
                string content,
                MessageDirection direction, DateTime timestamp)
            {
                if (string.IsNullOrWhiteSpace(fromPeer))
                    throw new ArgumentException(nameof(fromPeer));
                if (string.IsNullOrWhiteSpace(toPeer))
                    throw new ArgumentException(nameof(toPeer));
                if (string.IsNullOrWhiteSpace(content))
                    throw new ArgumentException(nameof(content));

                Id = Guid.NewGuid();
                FromPeer = fromPeer;
                ToPeer = toPeer;
                Content = content.Trim();
                Timestamp = timestamp;
                Direction = direction;
            }

            public override string ToString()
                => $"[{Timestamp:HH:mm:ss}] {FromPeer} → {ToPeer}: {Content}";
        }
}




