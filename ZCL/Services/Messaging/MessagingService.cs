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
    private readonly IPeerRepository _peers;
    private readonly IMessageRepository _messages;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private Task? _hostingTask;

    private NetworkStream? _stream;
    private Guid _currentSessionId;
    private string? _remotePeerId; // protocol GUID string

    public event Action<string>? SessionStarted;
    public event Action<ChatMessage>? MessageReceived;
    public event Action? SessionClosed;

    public MessagingService(ZcspPeer peer, IPeerRepository peers, IMessageRepository messages)
    {
        _peer = peer;
        _peers = peers;
        _messages = messages;

        // No UI button. Host silently.
        EnsureHostingStarted(5555);
    }

    public void EnsureHostingStarted(int port)
    {
        // idempotent: only starts once
        if (_hostingTask != null)
            return;

        _hostingTask = Task.Run(() =>
            _peer.StartHostingAsync(
                port,
                serviceName => serviceName == ServiceName ? this : null
            )
        );
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
        // Fast path (already connected to that peer)
        if (IsSessionActiveWith(remoteProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock
            if (IsSessionActiveWith(remoteProtocolPeerId))
                return;

            // Make sure we can receive incoming connections regardless
            EnsureHostingStarted(port);

            // Attempt connect with a small retry window.
            // This handles the “both peers started at the same time” case.
            var attempts = 4;
            var delayMs = 250;

            Exception? last = null;

            for (int i = 0; i < attempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await _peer.ConnectAsync(remoteIp, port, remoteProtocolPeerId, this);
                    // OnSessionStartedAsync will set _stream/_currentSessionId/_remotePeerId
                    if (IsSessionActiveWith(remoteProtocolPeerId))
                        return;

                    // If ConnectAsync returned but we’re not active, treat as failure
                    throw new InvalidOperationException("Connect completed but session is not active.");
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(delayMs, ct);
                }
            }

            throw new InvalidOperationException(
                $"Could not establish session to {remoteProtocolPeerId} @ {remoteIp}:{port}",
                last);
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

        if (_stream == null || _remotePeerId == null)
            throw new InvalidOperationException("Messaging session is not active.");

        var localPeer = await _peers.GetOrCreateAsync(_peer.PeerId, isLocal: true, ct: ct);
        var remotePeer = await _peers.GetOrCreateAsync(remoteProtocolPeerId, ipAddress: remoteIp, ct: ct);

        var entity = await _messages.StoreOutgoingAsync(
            _currentSessionId,
            localPeer.PeerId,
            remotePeer.PeerId,
            content);

        MessageReceived?.Invoke(ChatMessageMapper.Outgoing(_peer.PeerId, remoteProtocolPeerId, entity));

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

    // =====================
    // IZcspService
    // =====================

    public void BindStream(NetworkStream stream) => _stream = stream;

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

        var fromPeerEntity = await _peers.GetOrCreateAsync(fromPeer);
        var toPeerEntity = await _peers.GetOrCreateAsync(_peer.PeerId, isLocal: true);

        var entity = await _messages.StoreIncomingAsync(
            sessionId,
            fromPeerEntity.PeerId,
            toPeerEntity.PeerId,
            content);

        MessageReceived?.Invoke(ChatMessageMapper.Incoming(fromPeer, toPeer, entity));
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        _stream = null;
        _remotePeerId = null;
        _currentSessionId = Guid.Empty;

        SessionClosed?.Invoke();
        return Task.CompletedTask;
    }
}
