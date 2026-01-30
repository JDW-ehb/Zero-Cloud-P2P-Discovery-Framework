using System;

namespace ZCL.Services.Messaging
{
    public sealed class ChatMessage
    {
        public Guid Id { get; }
        public string FromPeer { get; }
        public string ToPeer { get; }
        public string Content { get; }
        public DateTime Timestamp { get; }

        public bool IsOutgoing { get; }
        public bool IsIncoming => !IsOutgoing;

        public ChatMessage(
            string fromPeer,
            string toPeer,
            string content,
            string localPeerId)
        {
            if (string.IsNullOrWhiteSpace(fromPeer))
                throw new ArgumentException(nameof(fromPeer));
            if (string.IsNullOrWhiteSpace(toPeer))
                throw new ArgumentException(nameof(toPeer));
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException(nameof(content));
            if (string.IsNullOrWhiteSpace(localPeerId))
                throw new ArgumentException(nameof(localPeerId));

            Id = Guid.NewGuid();
            FromPeer = fromPeer;
            ToPeer = toPeer;
            Content = content.Trim();
            Timestamp = DateTime.UtcNow;

            IsOutgoing = fromPeer == localPeerId;
        }

        public override string ToString()
            => $"[{Timestamp:HH:mm:ss}] {FromPeer} → {ToPeer}: {Content}";
    }


}
