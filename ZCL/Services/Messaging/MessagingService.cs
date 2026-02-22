using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
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
    private readonly ConcurrentDictionary<string, (Guid sessionId, NetworkStream stream)> _activePeers = new();

    public event Action<string>? SessionStarted;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? SessionClosed;


    public MessagingService(ZcspPeer peer, IServiceScopeFactory scopeFactory)
    {
        _peer = peer;
        _scopeFactory = scopeFactory;
    }

    private async Task UseScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = _scopeFactory.CreateScope();
        await action(scope.ServiceProvider);
    }

    private bool IsSessionActiveWith(string remoteProtocolPeerId)
        => _activePeers.ContainsKey(remoteProtocolPeerId);

    public async Task EnsureSessionAsync(
        string remoteProtocolPeerId,
        string remoteIp,
        int port,
        CancellationToken ct = default)
    {
        if (IsSessionActiveWith(remoteProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (IsSessionActiveWith(remoteProtocolPeerId))
                return;

            await _peer.ConnectAsync(remoteIp, port, remoteProtocolPeerId, this);

            if (!IsSessionActiveWith(remoteProtocolPeerId))
                throw new InvalidOperationException("Connect completed but session not active.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task SendMessageAsync(
        string remoteProtocolPeerId,
        string remoteIp,
        int port,
        string content,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        await EnsureSessionAsync(remoteProtocolPeerId, remoteIp, port, ct);

        if (!_activePeers.TryGetValue(remoteProtocolPeerId, out var conn))
            throw new InvalidOperationException("Messaging session is not active.");

        MessageEntity entity = default!;

        await UseScopeAsync(async sp =>
        {
            var peers = sp.GetRequiredService<IPeerRepository>();
            var messages = sp.GetRequiredService<IMessageRepository>();

            var localPeer = await peers.GetLocalPeerAsync(ct);
            var remotePeer = await peers.GetOrCreateAsync(
                remoteProtocolPeerId,
                ipAddress: remoteIp,
                ct: ct);

            entity = await messages.StoreOutgoingAsync(
                conn.sessionId,
                localPeer.PeerId,
                remotePeer.PeerId,
                content);
        });

        MessageReceived?.Invoke(
            ChatMessageMapper.Outgoing(_peer.PeerId, remoteProtocolPeerId, entity));

            var data = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            conn.sessionId,
                    w =>
            {
                BinaryCodec.WriteString(w, _peer.PeerId);
                BinaryCodec.WriteString(w, remoteProtocolPeerId);
                BinaryCodec.WriteString(w, content);
            });

        await Framing.WriteAsync(conn.stream, data);
    }


    public async Task OnSessionStartedAsync(Guid sessionId, string remotePeerId, NetworkStream stream)
    {
        _activePeers[remotePeerId] = (sessionId, stream);

        Console.WriteLine($"[Messaging] Session started with {remotePeerId}");

        await DeliverPendingMessagesAsync(remotePeerId);

        SessionStarted?.Invoke(remotePeerId);
    }

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        var fromPeer = BinaryCodec.ReadString(reader);
        var toPeer = BinaryCodec.ReadString(reader);
        var content = BinaryCodec.ReadString(reader);

        MessageEntity entity = default!;

        NetworkStream? targetStream = null;
        byte[]? forwardBytes = null;


        await UseScopeAsync(async sp =>
        {
            var peers = sp.GetRequiredService<IPeerRepository>();
            var messages = sp.GetRequiredService<IMessageRepository>();

            var fromPeerEntity = await peers.GetOrCreateAsync(fromPeer);
            var toPeerEntity = await peers.GetOrCreateAsync(toPeer);

            entity = await messages.StoreIncomingAsync(
                sessionId,
                fromPeerEntity.PeerId,
                toPeerEntity.PeerId,
                content);

            if (_activePeers.TryGetValue(toPeer, out var target))
            {
                targetStream = target.stream;

                forwardBytes = BinaryCodec.Serialize(
                    ZcspMessageType.SessionData,
                    target.sessionId,
                    w =>
                    {
                        BinaryCodec.WriteString(w, fromPeer);
                        BinaryCodec.WriteString(w, toPeer);
                        BinaryCodec.WriteString(w, content);
                    });
            }
        });

        if (targetStream != null && forwardBytes != null)
        {
            try
            {
                await Framing.WriteAsync(targetStream, forwardBytes);

                await UseScopeAsync(async sp =>
                {
                    var messages = sp.GetRequiredService<IMessageRepository>();
                    await messages.MarkAsDeliveredAsync(entity.MessageId);
                });
            }
            catch
            {
                // do NOT mark delivered
            }
        }

        MessageReceived?.Invoke(
            ChatMessageMapper.Incoming(fromPeer, toPeer, entity));
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        foreach (var kvp in _activePeers)
        {
            if (kvp.Value.sessionId == sessionId)
            {
                _activePeers.TryRemove(kvp.Key, out _);
                SessionClosed?.Invoke(kvp.Key);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private async Task DeliverPendingMessagesAsync(string protocolPeerId)
    {
        if (!_activePeers.TryGetValue(protocolPeerId, out var target))
            return;

        await UseScopeAsync(async sp =>
        {
            var peers = sp.GetRequiredService<IPeerRepository>();
            var messages = sp.GetRequiredService<IMessageRepository>();

            var peerEntity = await peers.GetOrCreateAsync(protocolPeerId);

            // You need a query like this in IMessageRepository
            var pending = await messages.GetUndeliveredMessagesAsync(peerEntity.PeerId);

            foreach (var msg in pending)
            {
                var fromPeer = await peers.GetByIdAsync(msg.FromPeerId);
                var toPeer = await peers.GetByIdAsync(msg.ToPeerId);

                if (fromPeer == null || toPeer == null)
                    continue;

                var forward = BinaryCodec.Serialize(
                    ZcspMessageType.SessionData,
                    target.sessionId,
                    w =>
                    {
                        BinaryCodec.WriteString(w, fromPeer.ProtocolPeerId);
                        BinaryCodec.WriteString(w, toPeer.ProtocolPeerId);
                        BinaryCodec.WriteString(w, msg.Content);
                    });

                await Framing.WriteAsync(target.stream, forward);

                await messages.MarkAsDeliveredAsync(msg.MessageId);
            }
        });
    }
}
