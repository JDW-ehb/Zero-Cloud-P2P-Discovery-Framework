using System;
using System.Collections.Concurrent;

namespace ZCL.Protocols.ZCSP.Sessions
{
    public sealed class SessionRegistry
    {
        private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

        public Session Create(string peerId, TimeSpan lifetime)
        {
            var session = new Session(
                Guid.NewGuid(),
                peerId,
                DateTime.UtcNow.Add(lifetime));

            _sessions[session.Id] = session;
            return session;
        }

        public bool TryGet(Guid sessionId, out Session session)
        {
            if (_sessions.TryGetValue(sessionId, out session!))
            {
                if (session.IsExpired)
                {
                    _sessions.TryRemove(sessionId, out _);
                    session = null!;
                    return false;
                }
                return true;
            }

            return false;
        }

        public bool Remove(Guid sessionId)
        {
            return _sessions.TryRemove(sessionId, out _);
        }

        public void CleanupExpired()
        {
            foreach (var (id, session) in _sessions)
            {
                if (session.IsExpired)
                    _sessions.TryRemove(id, out _);
            }
        }
    }
}
