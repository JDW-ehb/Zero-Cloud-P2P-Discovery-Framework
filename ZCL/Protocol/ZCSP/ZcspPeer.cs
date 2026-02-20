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

        // ACTIVE PEER MAP (ProtocolPeerId → Stream)
        private readonly ConcurrentDictionary<string, NetworkStream> _activePeers
            = new();

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
            using (client)
            using (var stream = client.GetStream())
            {
                var frame = await Framing.ReadAsync(stream);
                if (frame == null)
                    return;

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);

                if (type != ZcspMessageType.ServiceRequest)
                    return;

                reader.ReadBytes(16); // correlation id
                var fromPeer = BinaryCodec.ReadString(reader);
                BinaryCodec.ReadString(reader); // target peer (ignored here)
                var serviceName = BinaryCodec.ReadString(reader);

                Console.WriteLine($"[ZCSP] ServiceRequest from {fromPeer}");

                var service = serviceResolver(serviceName);
                if (service == null)
                    return;

                var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));

                // REGISTER PEER
                _activePeers[fromPeer] = stream;
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

                await RunSessionAsync(stream, session.Id, fromPeer, service);
            }
        }

        // =====================================
        // SESSION LOOP
        // =====================================

        private async Task RunSessionAsync(
    NetworkStream stream,
    Guid sessionId,
    string fromPeer,
    IZcspService service)
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
                        await service.OnSessionDataAsync(sessionId, reader);

                        var (_, _, _, routeReader) = BinaryCodec.Deserialize(frame);

                        var action = BinaryCodec.ReadString(routeReader);

                        if (action == "SendMessage")
                        {
                            var fromPeerId = BinaryCodec.ReadString(routeReader);
                            var toPeerId = BinaryCodec.ReadString(routeReader);

                            if (_activePeers.TryGetValue(toPeerId, out var targetStream))
                            {
                                Console.WriteLine($"[ZCSP] Forwarding {fromPeerId} → {toPeerId}");
                                await Framing.WriteAsync(targetStream, frame);
                            }
                            else
                            {
                                Console.WriteLine($"[ZCSP] Target {toPeerId} not connected.");
                            }
                        }
                    }
                }
            }
            finally
            {
                Console.WriteLine($"[ZCSP] Session closed → {fromPeer}");

                _activePeers.TryRemove(fromPeer, out _);
                _sessions.Remove(sessionId);
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

            var client = new TcpClient();
            await client.ConnectAsync(_coordinatorIp!, 5555, ct);

            var stream = client.GetStream();

            var request = BinaryCodec.Serialize(
                ZcspMessageType.ServiceRequest,
                null,
                w =>
                {
                    w.Write(Guid.NewGuid().ToByteArray());
                    BinaryCodec.WriteString(w, localId);
                    BinaryCodec.WriteString(w, remoteProtocolPeerId);
                    BinaryCodec.WriteString(w, service.ServiceName);
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

            _ = Task.Run(() => RunSessionAsync(stream, sessionId.Value, localId, service));
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
    }
}