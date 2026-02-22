using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Protocol.ZCSP.Transport;
using ZCL.Repositories.Peers;

namespace ZCL.Protocol.ZCSP
{
    public sealed class ZcspPeer
    {
        private string? _peerId; // resolved from DB (GUID string)
        private readonly SessionRegistry _sessions;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RoutingState _routing;

        public string PeerId => _peerId ?? "(unresolved)";

        public ZcspPeer(IServiceScopeFactory scopeFactory, SessionRegistry sessions, RoutingState routing)
        {
            _scopeFactory = scopeFactory;
            _sessions = sessions;
            _routing = routing;
        }

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
                throw new InvalidOperationException($"Local protocol peer id must be a GUID string, got '{_peerId}'.");

            return _peerId!;
        }

        // =====================
        // HOSTING (SERVER SIDE)
        // =====================

        public async Task StartHostingAsync(int port, Func<string, IZcspService?> serviceResolver)
        {
            var localId = await EnsurePeerIdAsync();

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{localId}] Hosting on port {port}");

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

        private async Task HandleClientAsync(TcpClient client, Func<string, IZcspService?> serviceResolver)
        {
            var localId = await EnsurePeerIdAsync();

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var frame = await Framing.ReadAsync(stream);
                    if (frame == null) return;

                    var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                    if (type != ZcspMessageType.ServiceRequest) return;

                    reader.ReadBytes(16); // requestId
                    var fromPeer = BinaryCodec.ReadString(reader);
                    var toPeer = BinaryCodec.ReadString(reader); // toPeer (keep it!)
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
                        w => { w.Write(true); w.Write(session.ExpiresAt.Ticks); });

                    await Framing.WriteAsync(stream, accept);

                    await service.OnSessionStartedAsync(session.Id, fromPeer, stream);

                    await RunSessionAsync(stream, session.Id, service);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{localId}] HandleClientAsync failed:");
                Console.WriteLine(ex);
            }
        }

        // =====================
        // CONNECTING (CLIENT SIDE)
        // =====================

        public async Task ConnectAsync(string host, int port, string remotePeerId, IZcspService service)
        {
            var localId = await EnsurePeerIdAsync();

            // ==========================
            // ROUTING ENFORCEMENT
            // ==========================
            // Final destination in the protocol (who we WANT to reach)
            var finalToPeerId = remotePeerId;

            // Actual TCP target (who we CONNECT to)
            var connectHost = host;
            var connectPort = port;

            if (_routing.Mode == RoutingMode.ViaServer)
            {
                if (string.IsNullOrWhiteSpace(_routing.ServerHost))
                    throw new InvalidOperationException("Routing is ViaServer but ServerHost is null.");

                if (_routing.ServerPort <= 0)
                    throw new InvalidOperationException("Routing is ViaServer but ServerPort is invalid.");

                connectHost = _routing.ServerHost!;
                connectPort = _routing.ServerPort;

                // Important: DO NOT change finalToPeerId.
                // We still want the message routed to the real remote peer.
            }

            var client = new TcpClient();
            NetworkStream? stream = null;

            try
            {
                Console.WriteLine($"[CONNECT] Mode={_routing.Mode} Connecting to {connectHost}:{connectPort} (finalTo={finalToPeerId})");
                await client.ConnectAsync(connectHost, connectPort);
                stream = client.GetStream();

                var request = BinaryCodec.Serialize(
                    ZcspMessageType.ServiceRequest,
                    null,
                    w =>
                    {
                        w.Write(Guid.NewGuid().ToByteArray());
                        BinaryCodec.WriteString(w, localId);
                        BinaryCodec.WriteString(w, finalToPeerId);
                        BinaryCodec.WriteString(w, service.ServiceName);
                    });

                await Framing.WriteAsync(stream, request);

                var frame = await Framing.ReadAsync(stream);
                if (frame == null)
                    throw new IOException("No service response (connection closed).");

                var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                    throw new InvalidOperationException("Invalid service response.");

                await service.OnSessionStartedAsync(sessionId.Value, finalToPeerId, stream);

                _ = Task.Run(async () =>
                {
                    try { await RunSessionAsync(stream, sessionId.Value, service); }
                    finally { stream.Dispose(); client.Dispose(); }
                });
            }
            catch
            {
                try { stream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
                throw;
            }
        }


        private async Task RunSessionAsync(NetworkStream stream, Guid sessionId, IZcspService service)
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
                    catch (IOException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
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
                // ALWAYS notify service, even if an exception happened mid-read or mid-handle
                try { await service.OnSessionClosedAsync(sessionId); }
                catch { /* don't let close cascade-crash */ }

                _sessions.Remove(sessionId);
            }
        }

    }
}
