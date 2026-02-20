using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ZCL.API;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Protocol.ZCSP.Transport;
using ZCL.Repositories.Peers;

namespace ZCL.Protocol.ZCSP
{
    public sealed class ZcspPeer
    {
        private string? _peerId;
        private readonly SessionRegistry _sessions;
        private readonly IServiceScopeFactory _scopeFactory;

        // =====================================
        // Coordinator: track active connections
        // ProtocolPeerId -> connection (TcpClient + NetworkStream)
        // =====================================
        private sealed class PeerConnection
        {
            public required TcpClient Client { get; init; }
            public required NetworkStream Stream { get; init; }
        }

        private readonly ConcurrentDictionary<string, PeerConnection> _activePeers = new();

        // =====================================
        // Client side: keep coordinator TcpClients alive
        // Otherwise GC can collect them and close sockets.
        // Key is "remoteProtocolPeerId|serviceName"
        // =====================================
        private readonly ConcurrentDictionary<string, TcpClient> _activeCoordinatorClients = new();

        private string? _coordinatorPeerId;
        private string? _coordinatorIp;

        public string PeerId => _peerId ?? "(unresolved)";

        public ZcspPeer(IServiceScopeFactory scopeFactory, SessionRegistry sessions)
        {
            _scopeFactory = scopeFactory;
            _sessions = sessions;
        }

        // =====================================
        // LOCAL ID
        // =====================================
        private async Task<string> EnsurePeerIdAsync(CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(_peerId))
                return _peerId!;

            using var scope = _scopeFactory.CreateScope();
            var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

            _peerId = await peers.GetOrCreateLocalProtocolPeerIdAsync(
                Config.Instance.PeerName,
                "127.0.0.1",
                ct);

            Console.WriteLine($"[ZCSP] Local PeerId resolved: {_peerId}");

            if (!Guid.TryParse(_peerId, out _))
                throw new InvalidOperationException("Local protocol peer id must be GUID.");

            return _peerId!;
        }

        // =====================================
        // HOSTING (Coordinator Mode)
        // =====================================
        public async Task StartHostingAsync(
            int port,
            Func<string, IZcspService?> serviceResolver)
        {
            var localId = await EnsurePeerIdAsync();

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[ZCSP] [{localId}] Hosting on TCP {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(client, serviceResolver); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[ZCSP] Client handler crashed:");
                        Console.WriteLine(ex);
                    }
                });
            }
        }

        private async Task HandleClientAsync(
            TcpClient client,
            Func<string, IZcspService?> serviceResolver)
        {
            // IMPORTANT:
            // Do NOT "using(client)" here, because we want to control lifetime explicitly.
            // We will dispose in the session loop finally block.
            var stream = client.GetStream();

            string? fromPeer = null;
            Guid sessionId = Guid.Empty;

            try
            {
                var frame = await Framing.ReadAsync(stream);
                if (frame == null)
                    return;

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);

                if (type != ZcspMessageType.ServiceRequest)
                    return;

                reader.ReadBytes(16); // correlation id
                fromPeer = BinaryCodec.ReadString(reader);
                BinaryCodec.ReadString(reader); // target peer (ignored on coordinator)
                var serviceName = BinaryCodec.ReadString(reader);

                Console.WriteLine($"[ZCSP] ServiceRequest from {fromPeer} (service={serviceName})");

                var service = serviceResolver(serviceName);
                if (service == null)
                    return;

                var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));
                sessionId = session.Id;

                // REGISTER PEER CONNECTION (ProtocolPeerId -> connection)
                _activePeers[fromPeer] = new PeerConnection
                {
                    Client = client,
                    Stream = stream
                };

                Console.WriteLine($"[ZCSP] Peer registered → {fromPeer}");

                var accept = BinaryCodec.Serialize(
                    ZcspMessageType.ServiceResponse,
                    session.Id,
                    w =>
                    {
                        w.Write(true);
                        w.Write(session.ExpiresAt.Ticks);
                    });

                await Framing.WriteAsync(stream, accept);

                service.BindStream(stream);
                await service.OnSessionStartedAsync(session.Id, fromPeer);

                await RunSessionAsync(
                    stream: stream,
                    sessionId: session.Id,
                    peerKey: fromPeer,
                    service: service,
                    isCoordinatorSession: true);
            }
            finally
            {
                // If we never completed handshake, close quietly
                // (RunSessionAsync does normal cleanup on established sessions)
                if (fromPeer == null || sessionId == Guid.Empty)
                {
                    try { stream.Dispose(); } catch { }
                    try { client.Close(); } catch { }
                }
            }
        }

        // =====================================
        // SESSION LOOP (shared for coordinator + client)
        // =====================================
        private async Task RunSessionAsync(
            NetworkStream stream,
            Guid sessionId,
            string peerKey,
            IZcspService service,
            bool isCoordinatorSession)
        {
            try
            {
                while (true)
                {
                    byte[]? frame;

                    try { frame = await Framing.ReadAsync(stream); }
                    catch { break; }

                    if (frame == null)
                        break;

                    var (type, _, _, reader) = BinaryCodec.Deserialize(frame);

                    if (type == ZcspMessageType.SessionClose)
                        break;

                    if (type == ZcspMessageType.SessionData)
                    {
                        // Let the service handle the payload first
                        await service.OnSessionDataAsync(sessionId, reader);

                        // Coordinator: route selected actions
                        if (isCoordinatorSession)
                        {
                            // Re-deserialize to re-read payload from the start
                            var (_, _, _, routeReader) = BinaryCodec.Deserialize(frame);

                            var action = BinaryCodec.ReadString(routeReader);

                            if (action == "SendMessage")
                            {
                                // IMPORTANT: this assumes MessagingService payload begins with:
                                // action, fromProtocolId, toProtocolId, ...
                                var fromPeerId = BinaryCodec.ReadString(routeReader);
                                var toPeerId = BinaryCodec.ReadString(routeReader);

                                if (_activePeers.TryGetValue(toPeerId, out var targetConn))
                                {
                                    Console.WriteLine($"[ZCSP] Forwarding {fromPeerId} → {toPeerId}");
                                    try
                                    {
                                        await Framing.WriteAsync(targetConn.Stream, frame);
                                    }
                                    catch
                                    {
                                        // recipient disconnected; cleanup map entry
                                        _activePeers.TryRemove(toPeerId, out var removed);
                                        try { removed?.Stream.Dispose(); } catch { }
                                        try { removed?.Client.Close(); } catch { }
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[ZCSP] Target {toPeerId} not connected.");
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                Console.WriteLine($"[ZCSP] Session closed → {peerKey}");

                // Coordinator cleanup: remove mapping and close the whole connection
                if (isCoordinatorSession)
                {
                    if (_activePeers.TryRemove(peerKey, out var conn))
                    {
                        try { conn.Stream.Dispose(); } catch { }
                        try { conn.Client.Close(); } catch { }
                    }
                }

                // Always remove session from registry (it is safe even if absent)
                _sessions.Remove(sessionId);

                // Service gets a close callback as well
                try { await service.OnSessionClosedAsync(sessionId); } catch { }
            }
        }

        // =====================================
        // OPEN SESSION (Client Side)
        // =====================================
        public async Task OpenSessionAsync(
            string remoteProtocolPeerId,
            IZcspService service,
            CancellationToken ct = default)
        {
            await EnsurePeerIdAsync(ct);

            if (await TryResolveCoordinatorAsync(ct) && _coordinatorIp != null)
            {
                await ConnectViaCoordinatorAsync(remoteProtocolPeerId, service, ct);
                return;
            }

            throw new InvalidOperationException("Coordinator not available.");
        }

        private async Task ConnectViaCoordinatorAsync(
            string remoteProtocolPeerId,
            IZcspService service,
            CancellationToken ct)
        {
            var localId = await EnsurePeerIdAsync(ct);

            // One persistent TCP connection per (remote peer, service)
            // (You can change this key if you want one connection per service only)
            var key = $"{remoteProtocolPeerId}|{service.ServiceName}";

            // Close existing if any (prevents leaks if user reconnects)
            if (_activeCoordinatorClients.TryRemove(key, out var old))
            {
                try { old.Close(); } catch { }
            }

            var client = new TcpClient();
            await client.ConnectAsync(_coordinatorIp!, 5555, ct);

            // Keep it alive to prevent GC closing it
            _activeCoordinatorClients[key] = client;

            var stream = client.GetStream();

            var request = BinaryCodec.Serialize(
                ZcspMessageType.ServiceRequest,
                null,
                w =>
                {
                    w.Write(Guid.NewGuid().ToByteArray());          // correlation id
                    BinaryCodec.WriteString(w, localId);            // from
                    BinaryCodec.WriteString(w, remoteProtocolPeerId); // to (logical target)
                    BinaryCodec.WriteString(w, service.ServiceName); // service
                });

            await Framing.WriteAsync(stream, request);

            var frame = await Framing.ReadAsync(stream);
            if (frame == null)
                throw new IOException("Coordinator closed connection.");

            var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);

            if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                throw new InvalidOperationException("Invalid coordinator response.");

            service.BindStream(stream);
            await service.OnSessionStartedAsync(sessionId.Value, remoteProtocolPeerId);

            // Client session loop (NOT coordinator routing; no _activePeers changes)
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSessionAsync(
                        stream: stream,
                        sessionId: sessionId.Value,
                        peerKey: localId,
                        service: service,
                        isCoordinatorSession: false);
                }
                finally
                {
                    // On disconnect, remove from keepalive map and close socket
                    _activeCoordinatorClients.TryRemove(key, out _);
                    try { stream.Dispose(); } catch { }
                    try { client.Close(); } catch { }
                }
            }, CancellationToken.None);
        }

        // =====================================
        // COORDINATOR RESOLUTION
        // =====================================
        private async Task<bool> TryResolveCoordinatorAsync(
            CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

            var coordinator = await peers.GetCoordinatorAsync(ct);
            if (coordinator == null)
                return false;

            _coordinatorPeerId = coordinator.ProtocolPeerId;
            _coordinatorIp = coordinator.IpAddress;

            return true;
        }

        // =====================================
        // Coordinator helper to send a raw frame to a peer
        // (Useful later if MessagingService wants coordinator transport delivery)
        // =====================================
        public Task<bool> TrySendToPeerAsync(string protocolPeerId, byte[] frame)
        {
            if (_activePeers.TryGetValue(protocolPeerId, out var conn))
            {
                return SendSafeAsync(conn, frame);
            }

            return Task.FromResult(false);

            static async Task<bool> SendSafeAsync(PeerConnection conn, byte[] frame)
            {
                try
                {
                    await Framing.WriteAsync(conn.Stream, frame);
                    return true;
                }
                catch
                {
                    try { conn.Stream.Dispose(); } catch { }
                    try { conn.Client.Close(); } catch { }
                    return false;
                }
            }
        }
    }
}