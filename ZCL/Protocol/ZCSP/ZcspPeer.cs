using Microsoft.Extensions.DependencyInjection;
using System.IO;
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
                hostName: Config.Instance.PeerName,
                ipAddress: "127.0.0.1",
                ct: ct);

            if (!Guid.TryParse(_peerId, out _))
                throw new InvalidOperationException(
                    $"Local protocol peer id must be a GUID string, got '{_peerId}'.");

            return _peerId!;
        }

        // =====================================
        // HOSTING (Coordinator)
        // =====================================

        public async Task StartHostingAsync(
            int port,
            Func<string, IZcspService?> serviceResolver)
        {
            var localId = await EnsurePeerIdAsync();

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{localId}] Hosting on TCP {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(client, serviceResolver); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{localId}] Client handler crashed:");
                        Console.WriteLine(ex);
                    }
                });
            }
        }

        private async Task HandleClientAsync(
            TcpClient client,
            Func<string, IZcspService?> serviceResolver)
        {
            var localId = await EnsurePeerIdAsync();

            using (client)
            using (var stream = client.GetStream())
            {
                var frame = await Framing.ReadAsync(stream);
                if (frame == null) return;

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceRequest) return;

                reader.ReadBytes(16); // requestId
                var fromPeer = BinaryCodec.ReadString(reader);
                BinaryCodec.ReadString(reader); // toPeer (logical target)
                var serviceName = BinaryCodec.ReadString(reader);

                var service = serviceResolver(serviceName);
                if (service == null)
                {
                    var reject = BinaryCodec.Serialize(
                        ZcspMessageType.ServiceResponse,
                        Guid.Empty,
                        w => { w.Write(false); w.Write(0L); });

                    await Framing.WriteAsync(stream, reject);
                    return;
                }

                var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));

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

                await RunSessionAsync(stream, session.Id, service);
            }
        }

        // =====================================
        // CLIENT CONNECT (always to coordinator)
        // =====================================

        public async Task OpenSessionAsync(
            string remoteProtocolPeerId,
            IZcspService service,
            CancellationToken ct = default)
        {
            var localId = await EnsurePeerIdAsync(ct);

            if (!await TryResolveCoordinatorAsync(ct))
                throw new InvalidOperationException(
                    "Coordinator unavailable. Hub model requires coordinator.");

            if (_coordinatorIp == null)
                throw new InvalidOperationException("Coordinator IP not resolved.");

            var client = new TcpClient();
            await client.ConnectAsync(_coordinatorIp, 5555, ct);

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

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSessionAsync(stream, sessionId.Value, service);
                }
                finally
                {
                    stream.Dispose();
                    client.Dispose();
                }
            });
        }

        // =====================================
        // SESSION LOOP
        // =====================================

        private async Task RunSessionAsync(
            NetworkStream stream,
            Guid sessionId,
            IZcspService service)
        {
            try
            {
                while (true)
                {
                    byte[]? frame;

                    try
                    {
                        frame = await Framing.ReadAsync(stream);
                    }
                    catch
                    {
                        break;
                    }

                    if (frame == null)
                        break;

                    var (type, _, _, reader) = BinaryCodec.Deserialize(frame);

                    if (type == ZcspMessageType.SessionClose)
                        break;

                    if (type == ZcspMessageType.SessionData)
                        await service.OnSessionDataAsync(sessionId, reader);
                }
            }
            finally
            {
                try { await service.OnSessionClosedAsync(sessionId); }
                catch { }

                _sessions.Remove(sessionId);
            }
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
        // Semantic entry point
        // =====================================

        public async Task StartRoutingHostAsync(
            int port,
            Func<string, IZcspService?> serviceResolver)
        {
            await StartHostingAsync(port, serviceResolver);
        }
    }
}