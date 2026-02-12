using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Services.Messaging;

public sealed class MessagingService
{
    private readonly ZcspPeer _peer;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly MessagingSessionHandler _handler;
    private readonly IServiceProvider _services;

    public event Action<string>? SessionStarted;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? SessionClosed;

    private sealed record SessionState(Guid SessionId, string RemotePeerId, NetworkStream Stream);
    private readonly Dictionary<string, SessionState> _sessionsByRemote = new();
    private readonly object _gate = new();

    public MessagingService(ZcspPeer peer, IServiceProvider services)
    {
        _peer = peer;
        _services = services;
    }

    private void Log(string msg)
        => Console.WriteLine($"[MessagingHub:{_peer.PeerId}] {msg}");

    // =====================================================
    // OUTBOUND CONNECTION
    // =====================================================

    public async Task EnsureSessionAsync(
        string remoteProtocolPeerId,
        string remoteIp,
        int port,
        CancellationToken ct = default)
    {
        if (HasSession(remoteProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (HasSession(remoteProtocolPeerId))
                return;

            Log($"Connecting to {remoteProtocolPeerId} @ {remoteIp}:{port}");


            var handler = _services.GetRequiredService<MessagingSessionHandler>();

            await _peer.ConnectAsync(
                remoteIp,
                port,
                remoteProtocolPeerId,
                handler);


        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool HasSession(string remote)
    {
        lock (_gate)
            return _sessionsByRemote.ContainsKey(remote);
    }

    // =====================================================
    // SEND
    // =====================================================

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

        SessionState state;

        lock (_gate)
        {
            if (!_sessionsByRemote.TryGetValue(remoteProtocolPeerId, out state!))
                throw new InvalidOperationException("No active session.");
        }

        var data = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            state.SessionId,
            w =>
            {
                BinaryCodec.WriteString(w, _peer.PeerId);
                BinaryCodec.WriteString(w, remoteProtocolPeerId);
                BinaryCodec.WriteString(w, content);
            });

        await Framing.WriteAsync(state.Stream, data);
    }

    // =====================================================
    // CALLED BY SESSION HANDLER
    // =====================================================

    internal Task InternalOnSessionStartedAsync(
        Guid sessionId,
        string remotePeerId,
        NetworkStream stream)
    {
        lock (_gate)
            _sessionsByRemote[remotePeerId] =
                new SessionState(sessionId, remotePeerId, stream);

        SessionStarted?.Invoke(remotePeerId);

        Log($"Session started with {remotePeerId}");
        return Task.CompletedTask;
    }

    internal Task InternalOnSessionClosedAsync(Guid sessionId)
    {
        string? remote = null;

        lock (_gate)
        {
            var kv = _sessionsByRemote.FirstOrDefault(x => x.Value.SessionId == sessionId);
            if (!string.IsNullOrEmpty(kv.Key))
            {
                remote = kv.Key;
                _sessionsByRemote.Remove(kv.Key);
            }
        }

        if (remote != null)
        {
            SessionClosed?.Invoke(remote);
            Log($"Session closed with {remote}");
        }

        return Task.CompletedTask;
    }

    internal Task InternalOnIncomingAsync(
        Guid sessionId,
        string fromPeer,
        string toPeer,
        string content)
    {
        var msg = new ChatMessage(
            id: Guid.NewGuid(),
            fromPeer: fromPeer,
            toPeer: toPeer,
            content: content,
            direction: MessageDirection.Incoming,
            timestamp: DateTime.UtcNow);

        MessageReceived?.Invoke(msg);
        return Task.CompletedTask;
    }
}
