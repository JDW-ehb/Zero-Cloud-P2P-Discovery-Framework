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
using ZCL.Repositories.Peers;
using ZCL.Repositories.Messages;

namespace ZCL.Services.Messaging
{
    public sealed class MessagingService : IZcspService
    {
        // =====================
        // Loading Repositories
        // =====================

        private readonly IPeerRepository _peers;
        private readonly IMessageRepository _messages;

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

        // =====================
        // Events
        // =====================
        public event Action<string>? SessionStarted;
        public event Action<ChatMessage>? MessageReceived;
        public event Action? SessionClosed;


        // =====================
        // Constructor
        // =====================

        public MessagingService(ZcspPeer peer, IPeerRepository peers, IMessageRepository messages)
        {
            _peer = peer;
            _peers = peers;
            _messages = messages;
        }



        // =====================
        // Public API (called from Main / UI)
        // =====================

        /// <summary>
        /// Initiate a messaging session to a remote peer using ZCSP.
        /// </summary>
        public Task ConnectToPeerAsync(string host, int port, string remotePeerId)
        {
            return _peer.ConnectAsync(host, port, remotePeerId, this);
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

            var localPeer = await _peers.GetOrCreateAsync(_peer.PeerId);
            var remotePeer = await _peers.GetOrCreateAsync(_remotePeerId);

            var entity = await _messages.StoreOutgoingAsync(
            _currentSessionId,
            localPeer.PeerId,
            remotePeer.PeerId,
            content);




            MessageReceived?.Invoke(
                ChatMessageMapper.Outgoing(
                    _peer.PeerId,
                    _remotePeerId!,
                    entity
                )
            );





            // Send over ZCSP (already exists)
            var data = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                _currentSessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, _peer.PeerId);   // sender
                    BinaryCodec.WriteString(w, _remotePeerId);  // receiver
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
            SessionStarted?.Invoke(remotePeerId);
            Console.WriteLine($"[Messaging] Session started with {remotePeerId}");
            return Task.CompletedTask;
        }

        



        public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
        {
            var fromPeer = BinaryCodec.ReadString(reader);
            var toPeer = BinaryCodec.ReadString(reader);
            var content = BinaryCodec.ReadString(reader);

            Console.WriteLine($"[Messaging] DATA from {fromPeer}: {content}");

            var fromPeerEntity = await _peers.GetOrCreateAsync(fromPeer);
            var toPeerEntity = await _peers.GetOrCreateAsync(_peer.PeerId);


            var entity = await _messages.StoreIncomingAsync(
            sessionId,
            fromPeerEntity.PeerId,
            toPeerEntity.PeerId,
            content);



            var msg = ChatMessageMapper.Incoming(
                fromPeer,
                toPeer,
                entity
            );

            Console.WriteLine(msg);

            MessageReceived?.Invoke(msg);
        }


        public Task OnSessionClosedAsync(Guid sessionId)
        {
            Console.WriteLine("[Messaging] Session closed");

            _stream = null;
            _remotePeerId = null;
            _currentSessionId = Guid.Empty;
            SessionClosed?.Invoke();
            return Task.CompletedTask;
        }

        public Task StartHostingAsync(int port)
        {
            return _peer.StartHostingAsync(
                port,
                serviceName => serviceName == ServiceName ? this : null
            );
        }

        
    }
}
