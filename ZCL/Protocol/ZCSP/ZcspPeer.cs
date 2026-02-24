using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Protocol.ZCSP.Transport;
using ZCL.Repositories.Peers;
using ZCL.Security;

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
                {
                    Stream? raw = null;
                    SslStream? tls = null;

                    try
                    {
                        raw = client.GetStream();

                        var cert = LoadLocalTlsIdentity();
                        tls = WrapServerTls(raw);

                        var serverOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = cert,
                            ClientCertificateRequired = true, 
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                        };

                        await tls.AuthenticateAsServerAsync(serverOptions);

                        var frame = await Framing.ReadAsync(tls);
                        if (frame == null) return;

                        var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                        if (type != ZcspMessageType.ServiceRequest) return;

                        reader.ReadBytes(16); 
                        var fromPeer = BinaryCodec.ReadString(reader);
                        var toPeer = BinaryCodec.ReadString(reader); 
                        var serviceName = BinaryCodec.ReadString(reader);

                        var service = serviceResolver(serviceName);
                        if (service == null)
                        {
                            var reject = BinaryCodec.Serialize(
                                ZcspMessageType.ServiceResponse,
                                Guid.Empty,
                                w => { w.Write(false); w.Write(0L); });

                            await Framing.WriteAsync(tls, reject);
                            return;
                        }

                        var session = _sessions.Create(fromPeer, TimeSpan.FromMinutes(30));

                        var accept = BinaryCodec.Serialize(
                            ZcspMessageType.ServiceResponse,
                            session.Id,
                            w => { w.Write(true); w.Write(session.ExpiresAt.Ticks); });

                        await Framing.WriteAsync(tls, accept);

                        await service.OnSessionStartedAsync(session.Id, fromPeer, tls);

                        await RunSessionAsync(tls, session.Id, service);
                    }
                    finally
                    {
                        try { tls?.Dispose(); } catch { }
                        try { raw?.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{localId}] HandleClientAsync failed:");
                Console.WriteLine(ex);
            }
        }

        public async Task ConnectAsync(string host, int port, string remotePeerId, IZcspService service)
        {
            var localId = await EnsurePeerIdAsync();

            var finalToPeerId = remotePeerId;

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

            }

            var client = new TcpClient();

            try
            {
                Console.WriteLine($"[CONNECT] Mode={_routing.Mode} Connecting to {connectHost}:{connectPort} (finalTo={finalToPeerId})");
                await client.ConnectAsync(connectHost, connectPort);

                var raw = client.GetStream();

                var myCert = LoadLocalTlsIdentity();
                var tls = WrapClientTls(raw); 

                var clientOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = connectHost,
                    ClientCertificates = new X509CertificateCollection { myCert },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };

                await tls.AuthenticateAsClientAsync(clientOptions);

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

                await Framing.WriteAsync(tls, request);

                var frame = await Framing.ReadAsync(tls);
                if (frame == null)
                    throw new IOException("No service response (connection closed).");

                var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                    throw new InvalidOperationException("Invalid service response.");

                await service.OnSessionStartedAsync(sessionId.Value, finalToPeerId, tls);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunSessionAsync(tls, sessionId.Value, service);
                    }
                    finally
                    {
                        try { tls.Dispose(); } catch { }
                        try { raw.Dispose(); } catch { }
                        try { client.Dispose(); } catch { }
                    }
                });
            }
            catch
            {
                try { client.Dispose(); } catch { }
                throw;
            }
        }

        private async Task RunSessionAsync(Stream stream, Guid sessionId, IZcspService service)
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
                try { await service.OnSessionClosedAsync(sessionId); }
                catch {  }

                _sessions.Remove(sessionId);
            }
        }



        private X509Certificate2 LoadLocalTlsIdentity()
        {

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return TlsCertificateProvider.LoadOrCreateIdentityCertificate(
                baseDirectory: baseDir,
                peerLabel: Config.Instance.PeerName);
        }

        private SslStream WrapServerTls(Stream raw)
        {
            return new SslStream(
                raw,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, cert, chain, errors) =>
                {
                    var x509 = cert as X509Certificate2 ?? (cert != null ? new X509Certificate2(cert) : null);

                    var ok = TlsValidation.IsTrustedPeerCertificate(x509, out var reason);
                    if (!ok)
                        Console.WriteLine($"[TLS] Rejecting client cert: {reason}");

                    return ok;
                });
        }

        private SslStream WrapClientTls(Stream raw)
        {
            return new SslStream(
                raw,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, cert, chain, errors) =>
                {
                    var x509 = cert as X509Certificate2 ?? (cert != null ? new X509Certificate2(cert) : null);

                    var ok = TlsValidation.IsTrustedPeerCertificate(x509, out var reason);
                    if (!ok)
                        Console.WriteLine($"[TLS] Rejecting server cert: {reason}");

                    return ok;
                });
        }
    }
}