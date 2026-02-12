using System.IO;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
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

    private async Task UseScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = _scopeFactory.CreateScope();
        await action(scope.ServiceProvider);
    }

    private bool IsSessionActiveWith(string remoteProtocolPeerId)
        => _stream != null &&
           _currentSessionId != Guid.Empty &&
           _remotePeerId == remoteProtocolPeerId;

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

        if (_stream == null)
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
                _currentSessionId,
                localPeer.PeerId,
                remotePeer.PeerId,
                content);
        });

        MessageReceived?.Invoke(
            ChatMessageMapper.Outgoing(_peer.PeerId, remoteProtocolPeerId, entity));

        var data = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _currentSessionId,
            w =>
            {
                BinaryCodec.WriteString(w, _peer.PeerId);
                BinaryCodec.WriteString(w, remoteProtocolPeerId);
                BinaryCodec.WriteString(w, content);
            });

        await Framing.WriteAsync(_stream, data);
    }

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
        var fromPeer = BinaryCodec.ReadString(reader);
        var toPeer = BinaryCodec.ReadString(reader);
        var content = BinaryCodec.ReadString(reader);

        MessageEntity entity = default!;

        await UseScopeAsync(async sp =>
        {
            var peers = sp.GetRequiredService<IPeerRepository>();
            var messages = sp.GetRequiredService<IMessageRepository>();

            var fromPeerEntity = await peers.GetOrCreateAsync(fromPeer);
            var toPeerEntity = await peers.GetLocalPeerAsync();

            entity = await messages.StoreIncomingAsync(
                sessionId,
                fromPeerEntity.PeerId,
                toPeerEntity.PeerId,
                content);
        });

        MessageReceived?.Invoke(
            ChatMessageMapper.Incoming(fromPeer, toPeer, entity));
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        var remote = _remotePeerId;

        try
        {
            _stream?.Dispose();
        }
        catch { }

        _stream = null;
        _currentSessionId = Guid.Empty;
        _remotePeerId = null;

        if (remote != null)
            SessionClosed?.Invoke(remote);

        return Task.CompletedTask;
    }


}
