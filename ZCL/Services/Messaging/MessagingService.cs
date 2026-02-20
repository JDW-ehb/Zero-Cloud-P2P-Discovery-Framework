using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;

namespace ZCL.Services.Messaging;

public sealed class MessagingService : IZcspService
{
    public string ServiceName => "Messaging";

    private readonly ZcspPeer _peer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private NetworkStream? _stream;
    private Guid _currentSessionId;
    private string? _remotePeerId;

    public event Action<string>? SessionStarted;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? SessionClosed;

    public MessagingService(ZcspPeer peer, IServiceScopeFactory scopeFactory)
    {
        _peer = peer;
        _scopeFactory = scopeFactory;
    }

    // =====================================
    // INTERNAL HELPER
    // =====================================

    private async Task UseScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = _scopeFactory.CreateScope();
        await action(scope.ServiceProvider);
    }

    private bool IsSessionActiveWith(string remoteProtocolPeerId)
        => _stream != null &&
           _currentSessionId != Guid.Empty &&
           _remotePeerId == remoteProtocolPeerId;

    // =====================================
    // SESSION MANAGEMENT
    // =====================================

    public async Task EnsureSessionAsync(
        string remoteProtocolPeerId,
        CancellationToken ct = default)
    {
        if (IsSessionActiveWith(remoteProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (IsSessionActiveWith(remoteProtocolPeerId))
                return;

            await _peer.OpenSessionAsync(remoteProtocolPeerId, this, ct);

            if (!IsSessionActiveWith(remoteProtocolPeerId))
                throw new InvalidOperationException("Session opened but not active.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // =====================================
    // SEND MESSAGE (CLIENT SIDE)
    // =====================================

    public async Task SendMessageAsync(
        string remoteProtocolPeerId,
        string content,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        await EnsureSessionAsync(remoteProtocolPeerId, ct);

        if (_stream == null)
            throw new InvalidOperationException("Messaging session is not active.");

        var clientMessageId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;

        MessageEntity entity = default!;
        string localProtocolId = string.Empty;

        // Store locally (optimistic UI)
        await UseScopeAsync(async sp =>
        {
            var peers = sp.GetRequiredService<IPeerRepository>();
            var messages = sp.GetRequiredService<IMessageRepository>();

            var localPeer = await peers.GetLocalPeerAsync(ct);
            localProtocolId = localPeer.ProtocolPeerId;

            var remotePeer = await peers.GetOrCreateAsync(remoteProtocolPeerId, ct: ct);

            entity = await messages.StoreOutgoingAsync(
                _currentSessionId,
                localPeer.PeerId,
                remotePeer.PeerId,
                content);
        });

        // Emit optimistic outgoing message
        MessageReceived?.Invoke(
            ChatMessageMapper.Outgoing(
                localProtocolId,
                remoteProtocolPeerId,
                entity));

        // Send to coordinator
        var data = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _currentSessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "SendMessage");
                BinaryCodec.WriteString(w, localProtocolId);
                BinaryCodec.WriteString(w, remoteProtocolPeerId);
                BinaryCodec.WriteString(w, clientMessageId.ToString());
                BinaryCodec.WriteString(w, content);
                w.Write(timestamp.Ticks);
            });

        await Framing.WriteAsync(_stream, data);
    }

    // =====================================
    // IZcspService IMPLEMENTATION
    // =====================================

    public void BindStream(NetworkStream stream)
    {
        _stream = stream;
    }

    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
    {
        _currentSessionId = sessionId;
        _remotePeerId = remotePeerId;

        SessionStarted?.Invoke(remotePeerId);
        return Task.CompletedTask;
    }

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        var action = BinaryCodec.ReadString(reader);

        // =====================================
        // COORDINATOR MODE
        // (Store + ACK only — routing handled in ZcspPeer)
        // =====================================
        if (Config.Instance.IsCoordinator)
        {
            if (action != "SendMessage")
                return;

            var fromProtocolId = BinaryCodec.ReadString(reader);
            var toProtocolId = BinaryCodec.ReadString(reader);
            var clientMessageId = BinaryCodec.ReadString(reader);
            var content = BinaryCodec.ReadString(reader);
            var timestampTicks = reader.ReadInt64();

            long serverTimestamp = DateTime.UtcNow.Ticks;

            await UseScopeAsync(async sp =>
            {
                var peers = sp.GetRequiredService<IPeerRepository>();
                var messages = sp.GetRequiredService<IMessageRepository>();

                var fromPeer = await peers.GetOrCreateAsync(fromProtocolId);
                var toPeer = await peers.GetOrCreateAsync(toProtocolId);

                await messages.StoreAsync(
                    sessionId,
                    fromPeer.PeerId,
                    toPeer.PeerId,
                    content,
                    MessageStatus.Sent);
            });

            // ACK sender
            if (_stream != null)
            {
                var ack = BinaryCodec.Serialize(
                    ZcspMessageType.SessionData,
                    sessionId,
                    w =>
                    {
                        BinaryCodec.WriteString(w, "Ack");
                        BinaryCodec.WriteString(w, clientMessageId);
                        w.Write(serverTimestamp);
                    });

                await Framing.WriteAsync(_stream, ack);
            }

            return;
        }

        // =====================================
        // CLIENT MODE
        // =====================================

        if (action == "Ack")
        {
            var clientMessageId = BinaryCodec.ReadString(reader);
            var serverTimestamp = reader.ReadInt64();

            // Optional: update message state to Delivered
            return;
        }

        if (action == "SendMessage")
        {
            var fromProtocolId = BinaryCodec.ReadString(reader);
            var toProtocolId = BinaryCodec.ReadString(reader);
            var clientMessageId = BinaryCodec.ReadString(reader);
            var content = BinaryCodec.ReadString(reader);
            var timestampTicks = reader.ReadInt64();

            MessageEntity entity = default!;

            await UseScopeAsync(async sp =>
            {
                var peers = sp.GetRequiredService<IPeerRepository>();
                var messages = sp.GetRequiredService<IMessageRepository>();

                var fromPeerEntity = await peers.GetOrCreateAsync(fromProtocolId);
                var localPeer = await peers.GetLocalPeerAsync();

                entity = await messages.StoreIncomingAsync(
                    sessionId,
                    fromPeerEntity.PeerId,
                    localPeer.PeerId,
                    content);
            });

            MessageReceived?.Invoke(
                ChatMessageMapper.Incoming(
                    fromProtocolId,
                    toProtocolId,
                    entity));

            return;
        }
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        var remote = _remotePeerId;

        try { _stream?.Dispose(); } catch { }

        _stream = null;
        _currentSessionId = Guid.Empty;
        _remotePeerId = null;

        if (remote != null)
            SessionClosed?.Invoke(remote);

        return Task.CompletedTask;
    }
}