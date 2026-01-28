using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
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
        // Constructor
        // =====================

        public MessagingService(ZcspPeer peer)
        {
            _peer = peer;
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

            _stream = null;
            _remotePeerId = null;
            _currentSessionId = Guid.Empty;

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
            => string.CompareOrdinal(a, b) < 0
                ? $"{a}|{b}"
                : $"{b}|{a}";
    }
}
