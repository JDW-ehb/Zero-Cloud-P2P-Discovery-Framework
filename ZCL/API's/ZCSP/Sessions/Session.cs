using System;

namespace ZCL.APIs.ZCSP.Sessions
{
    public sealed class Session
    {
        public Guid Id { get; }
        public string PeerId { get; }
        public DateTime ExpiresAt { get; private set; }

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

        public Session(Guid id, string peerId, DateTime expiresAt)
        {
            Id = id;
            PeerId = peerId;
            ExpiresAt = expiresAt;
        }

        public void Extend(TimeSpan duration)
        {
            ExpiresAt = DateTime.UtcNow.Add(duration);
        }
    }
}
