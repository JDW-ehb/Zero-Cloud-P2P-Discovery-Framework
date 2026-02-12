using Microsoft.Extensions.DependencyInjection;
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
        private string? _peerId; // resolved from DB (GUID string)
        private readonly SessionRegistry _sessions;
        private readonly IServiceScopeFactory _scopeFactory;

        public string PeerId => _peerId ?? "(unresolved)";

        public ZcspPeer(IServiceScopeFactory scopeFactory, SessionRegistry sessions)
        {
            _scopeFactory = scopeFactory;
            _sessions = sessions;
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
                    BinaryCodec.ReadString(reader); // toPeer
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

                    service.BindStream(stream);
                    await service.OnSessionStartedAsync(session.Id, fromPeer);

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

            var client = new TcpClient();
            NetworkStream? stream = null;

            try
            {
               await client.ConnectAsync(host, port);
                stream = client.GetStream();

                var request = BinaryCodec.Serialize(
                    ZcspMessageType.ServiceRequest,
                    null,
                    w =>
                    {
                        w.Write(Guid.NewGuid().ToByteArray());
                        BinaryCodec.WriteString(w, localId);
                        BinaryCodec.WriteString(w, remotePeerId);
                        BinaryCodec.WriteString(w, service.ServiceName);
                    });

                await Framing.WriteAsync(stream, request);

                var frame = await Framing.ReadAsync(stream);
                if (frame == null)
                    throw new IOException("No service response (connection closed).");

                var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                    throw new InvalidOperationException("Invalid service response.");

                service.BindStream(stream);
                await service.OnSessionStartedAsync(sessionId.Value, remotePeerId);

                _ = Task.Run(async () =>
                {
                    try { await RunSessionAsync(stream, sessionId.Value, service); }
                    finally { stream.Dispose(); client.Dispose(); }
                });
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[ZCSP] Connection failed: {ex.Message}");

                try { stream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }

                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZCSP] Unexpected connection error:");
                Console.WriteLine(ex);

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
