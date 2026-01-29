using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Services.Messaging
{
    public sealed class MessagingService : IZcspService
    {
        // =====================
        // Protocol identity
        // =====================

        public string ServiceName => "Messaging";

        // =====================
        // Dependency (protocol)
        // =====================

        private readonly ZcspPeer _peer;

        // =====================
        // Runtime state
        // =====================

        private NetworkStream? _stream;
        private Guid _currentSessionId;
        private string? _remotePeerId;

        private readonly ConcurrentDictionary<string, List<ChatMessage>> _messages = new();

        // =====================
        // Events
        // =====================

        public event Action<ChatMessage>? MessageReceived;

        // =====================
        // DBContext
        // =====================

        private readonly ServiceDBContext _db;

        private static readonly SemaphoreSlim _dbLock = new(1, 1);


        // =====================
        // Constructor
        // =====================

        public MessagingService(ZcspPeer peer, ServiceDBContext db)
        {
            _peer = peer;
            _db = db;
        }


        // =====================
        // Public API (called from Main / UI)
        // =====================

        /// <summary>
        /// Initiate a messaging session to a remote peer using ZCSP.
        /// </summary>
        public Task ConnectToPeerAsync(string host, int port)
        {
            return _peer.ConnectAsync(host, port, this);
        }

        /// <summary>
        /// Send a chat message inside an active session.
        /// </summary>
        public async Task SendMessageAsync(string content)
        {
            if (_stream == null || _remotePeerId == null)
                throw new InvalidOperationException("Messaging session is not active.");

            if (string.IsNullOrWhiteSpace(content))
                return;

            var peerGuid = await GetOrCreatePeerAsync(_remotePeerId);

            var entity = new MessageEntity
            {
                MessageId = Guid.NewGuid(),
                PeerId = peerGuid,
                SessionId = _currentSessionId,
                Content = content,
                Timestamp = DateTime.UtcNow,
                Direction = MessageDirection.Outgoing,
                Status = MessageStatus.Sent
            };


            await _dbLock.WaitAsync();
            try
            {
                _db.Messages.Add(entity);
                await _db.SaveChangesAsync();
            }
            finally
            {
                _dbLock.Release();
            }


            // 2️⃣ Update UI immediately
            MessageReceived?.Invoke(new ChatMessage(
                "local",
                _remotePeerId,
                content
            ));

            // 3️⃣ Send over ZCSP (already exists)
            var data = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                _currentSessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, "local");
                    BinaryCodec.WriteString(w, _remotePeerId);
                    BinaryCodec.WriteString(w, content);
                });

            await Framing.WriteAsync(_stream, data);
        }


        // =====================
        // IZcspService implementation
        // =====================

        public void BindStream(NetworkStream stream)
        {
            _stream = stream;
        }

        public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
        {
            _currentSessionId = sessionId;
            _remotePeerId = remotePeerId;

            Console.WriteLine($"[Messaging] Session started with {remotePeerId}");
            return Task.CompletedTask;
        }

        public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
        {
            var fromPeer = BinaryCodec.ReadString(reader);
            var toPeer = BinaryCodec.ReadString(reader);
            var content = BinaryCodec.ReadString(reader);

            if (fromPeer == "local")
                return;

            var peerGuid = await GetOrCreatePeerAsync(fromPeer);

            var entity = new MessageEntity
            {
                MessageId = Guid.NewGuid(),
                PeerId = peerGuid,
                SessionId = sessionId,
                Content = content,
                Timestamp = DateTime.UtcNow,
                Direction = MessageDirection.Incoming,
                Status = MessageStatus.Delivered
            };

            await _dbLock.WaitAsync();
            try
            {
                _db.Messages.Add(entity);
                await _db.SaveChangesAsync();
            }
            finally
            {
                _dbLock.Release();
            }


            var msg = Store(fromPeer, toPeer, content);
            Console.WriteLine(msg);

            MessageReceived?.Invoke(msg);
        }


        public Task OnSessionClosedAsync(Guid sessionId)
        {
            Console.WriteLine("[Messaging] Session closed");

            _stream = null;
            _remotePeerId = null;
            _currentSessionId = Guid.Empty;

            return Task.CompletedTask;
        }

        public Task StartHostingAsync(int port)
        {
            return _peer.StartHostingAsync(
                port,
                serviceName => serviceName == ServiceName ? this : null
            );
        }

        // =====================
        // usingdb ir creating peers
        // =====================

        private async Task<Guid> GetOrCreatePeerAsync(string protocolPeerId)
        {
            await _dbLock.WaitAsync();
            try
            {
                var peer = await _db.Peers
                    .FirstOrDefaultAsync(p => p.ProtocolPeerId == protocolPeerId);

                if (peer != null)
                {
                    peer.LastSeen = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return peer.PeerId;
                }

                peer = new PeerNode
                {
                    PeerId = Guid.NewGuid(),
                    ProtocolPeerId = protocolPeerId,
                    IpAddress = "unknown",
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    OnlineStatus = PeerOnlineStatus.Unknown
                };

                _db.Peers.Add(peer);
                await _db.SaveChangesAsync();

                return peer.PeerId;
            }
            finally
            {
                _dbLock.Release();
            }
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
            => string.CompareOrdinal(a, b) < 0
                ? $"{a}|{b}"
                : $"{b}|{a}";
    }
}
