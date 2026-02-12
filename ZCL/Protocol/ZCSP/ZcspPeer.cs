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
        private string? _peerId;
        private readonly SessionRegistry _sessions;
        private readonly IServiceScopeFactory _scopeFactory;

        public string PeerId => _peerId ?? "(unresolved)";

        public ZcspPeer(IServiceScopeFactory scopeFactory, SessionRegistry sessions)
        {
            _scopeFactory = scopeFactory;
            _sessions = sessions;
        }

        private void Log(string msg)
            => Console.WriteLine($"[ZCSP:{PeerId}] {msg}");

        private async Task<string> EnsurePeerIdAsync(CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(_peerId))
                return _peerId!;

            using var scope = _scopeFactory.CreateScope();
            var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

            _peerId = await peers.GetOrCreateLocalProtocolPeerIdAsync(
                hostName: Config.peerName,
                ipAddress: "127.0.0.1",
                ct: ct);

            if (!Guid.TryParse(_peerId, out _))
                throw new InvalidOperationException($"Local protocol peer id must be GUID string, got '{_peerId}'.");

            Console.WriteLine($"[ZCSP:{_peerId}] Local PeerId resolved.");

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

            Log($"Hosting on port {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                Log("Inbound TCP connection accepted.");

                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(client, serviceResolver); }
                    catch (Exception ex)
                    {
                        Log("Client handler crashed:");
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
                    Log("Reading inbound ServiceRequest...");

                    var frame = await Framing.ReadAsync(stream);
                    if (frame == null)
                    {
                        Log("Inbound connection closed before ServiceRequest.");
                        return;
                    }

                    var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                    if (type != ZcspMessageType.ServiceRequest)
                    {
                        Log($"Unexpected message type: {type}");
                        return;
                    }

                    reader.ReadBytes(16); // requestId
                    var fromPeer = BinaryCodec.ReadString(reader);
                    BinaryCodec.ReadString(reader); // toPeer
                    var serviceName = BinaryCodec.ReadString(reader);

                    Log($"ServiceRequest from {fromPeer} for service '{serviceName}'");

                    var service = serviceResolver(serviceName);
                    if (service == null)
                    {
                        Log($"Service '{serviceName}' not found. Rejecting.");

                        var reject = BinaryCodec.Serialize(
                            ZcspMessageType.ServiceResponse,
                            null,
                            w => { w.Write(false); w.Write(0L); });

                        await Framing.WriteAsync(stream, reject);
                        return;
                    }

                    var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));

                    Log($"Inbound session created | SessionId={session.Id}");

                    var accept = BinaryCodec.Serialize(
                        ZcspMessageType.ServiceResponse,
                        session.Id,
                        w => { w.Write(true); w.Write(session.ExpiresAt.Ticks); });

                    await Framing.WriteAsync(stream, accept);

                    Log("ServiceResponse ACCEPT sent.");

                    service.BindStream(stream);
                    await service.OnSessionStartedAsync(session.Id, fromPeer);

                    Log("Inbound session bound to service.");

                    await RunSessionAsync(stream, session.Id, service);
                }
            }
            catch (Exception ex)
            {
                Log("HandleClientAsync failed:");
                Console.WriteLine(ex);
            }
        }

        // =====================
        // CONNECTING (CLIENT SIDE)
        // =====================

        public async Task ConnectAsync(string host, int port, string remotePeerId, IZcspService service)
        {
            var localId = await EnsurePeerIdAsync();

            Log($"ConnectAsync → {remotePeerId} @ {host}:{port}");

            var client = new TcpClient();
            NetworkStream? stream = null;

            try
            {
                await client.ConnectAsync(host, port);
                stream = client.GetStream();

                Log("TCP connection established.");

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

                Log("ServiceRequest sent.");

                var frame = await Framing.ReadAsync(stream);
                if (frame == null)
                    throw new IOException("No service response (connection closed).");

                var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);

                if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                    throw new InvalidOperationException("Invalid service response.");

                Log($"ServiceResponse received | SessionId={sessionId}");

                service.BindStream(stream);
                await service.OnSessionStartedAsync(sessionId.Value, remotePeerId);

                Log("Outbound session bound to service.");

                _ = Task.Run(async () =>
                {
                    try { await RunSessionAsync(stream, sessionId.Value, service); }
                    finally
                    {
                        Log("Outbound session ended.");
                        stream.Dispose();
                        client.Dispose();
                    }
                });
            }
            catch (SocketException ex)
            {
                Log($"Connection failed: {ex.Message}");

                try { stream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }

                throw;
            }
            catch (Exception ex)
            {
                Log("Unexpected connection error:");
                Console.WriteLine(ex);

                try { stream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }

                throw;
            }
        }

        // =====================
        // SESSION LOOP
        // =====================

        private async Task RunSessionAsync(NetworkStream stream, Guid sessionId, IZcspService service)
        {
            Log($"RunSessionAsync started | SessionId={sessionId}");

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
                        Log("Stream IO exception. Closing session.");
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        Log("Stream disposed. Closing session.");
                        break;
                    }

                    if (frame == null)
                    {
                        Log("Frame null (remote closed).");
                        break;
                    }

                    var (type, _, _, reader) = BinaryCodec.Deserialize(frame);

                    Log($"Frame received | Type={type}");

                    if (type == ZcspMessageType.SessionClose)
                    {
                        Log("SessionClose received.");
                        break;
                    }

                    if (type == ZcspMessageType.SessionData)
                        await service.OnSessionDataAsync(sessionId, reader);
                }
            }
            finally
            {
                Log($"Session closing | SessionId={sessionId}");

                try { await service.OnSessionClosedAsync(sessionId); }
                catch { }

                _sessions.Remove(sessionId);

                Log($"Session removed from registry.");
            }
        }
    }
}
