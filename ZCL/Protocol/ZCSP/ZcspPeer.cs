using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;
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
        private string? _peerId;
        private readonly SessionRegistry _sessions;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RoutingState _routing;
        private readonly ISharedSecretProvider _secretProvider;

        public string PeerId => _peerId ?? "(unresolved)";

        public ZcspPeer(
            IServiceScopeFactory scopeFactory,
            SessionRegistry sessions,
            RoutingState routing,
            ISharedSecretProvider secretProvider)
        {
            _scopeFactory = scopeFactory;
            _sessions = sessions;
            _routing = routing;
            _secretProvider = secretProvider;
        }

        // =========================================================
        // Peer Identity
        // =========================================================

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

        // =========================================================
        // Hosting
        // =========================================================

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
                    using var raw = client.GetStream();

                    var cert = await LoadLocalTlsIdentityAsync();
                    using var tls = WrapServerTls(raw);

                    var serverOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    };

                    await WithTimeout(
                        tls.AuthenticateAsServerAsync(serverOptions),
                        TimeSpan.FromSeconds(8),
                        "TLS server handshake");

                    var frame = await WithTimeout(
                        Framing.ReadAsync(tls),
                        TimeSpan.FromSeconds(8),
                        "Initial frame read");

                    if (frame == null) return;

                    var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                    if (type != ZcspMessageType.ServiceRequest) return;

                    reader.ReadBytes(16);
                    var fromPeer = BinaryCodec.ReadString(reader);
                    BinaryCodec.ReadString(reader); // toPeer (unused here)
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{localId}] HandleClientAsync failed:");
                Console.WriteLine(ex);
            }
        }

        // =========================================================
        // Client Connect
        // =========================================================

        public async Task ConnectAsync(string host, int port, string remotePeerId, IZcspService service)
        {
            var localId = await EnsurePeerIdAsync();

            var connectHost = host;
            var connectPort = port;

            if (_routing.Mode == RoutingMode.ViaServer)
            {
                connectHost = _routing.ServerHost!;
                connectPort = _routing.ServerPort;
            }

            var client = new TcpClient();

            Console.WriteLine($"[CONNECT] Mode={_routing.Mode} Connecting to {connectHost}:{connectPort}");

            await WithTimeout(
                client.ConnectAsync(connectHost, connectPort),
                TimeSpan.FromSeconds(5),
                "TCP connect");

            var raw = client.GetStream();
            var tls = WrapClientTls(raw);

            var myCert = await LoadLocalTlsIdentityAsync();

            var clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = connectHost,
                ClientCertificates = new X509CertificateCollection { myCert },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };

            await WithTimeout(
                tls.AuthenticateAsClientAsync(clientOptions),
                TimeSpan.FromSeconds(8),
                "TLS client handshake");

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

            await Framing.WriteAsync(tls, request);

            var frame = await WithTimeout(
                Framing.ReadAsync(tls),
                TimeSpan.FromSeconds(8),
                "ServiceResponse read");

            if (frame == null)
                throw new IOException("No service response (connection closed).");

            var (type, sessionId, _, _) = BinaryCodec.Deserialize(frame);

            if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                throw new InvalidOperationException("Invalid service response.");

            await service.OnSessionStartedAsync(sessionId.Value, remotePeerId, tls);

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSessionAsync(tls, sessionId.Value, service);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[SESSION LOOP CRASH]");
                    Console.WriteLine(ex);
                }
                finally
                {
                    try { tls.Dispose(); } catch { }
                    try { raw.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                }
            });
        }

        // =========================================================
        // Session Loop
        // =========================================================

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

        // =========================================================
        // TLS
        // =========================================================

        private async Task<X509Certificate2> LoadLocalTlsIdentityAsync()
        {
            var secret = await _secretProvider.GetSecretAsync();

            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("TLS secret not set. Pairing required.");

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return TlsCertificateProvider.LoadOrCreateIdentityCertificate(
                baseDirectory: baseDir,
                sharedSecret: secret,
                peerLabel: Config.Instance.PeerName);
        }

        private SslStream WrapServerTls(Stream raw)
        {
            return new SslStream(raw, false,
                (sender, cert, chain, errors) =>
                {
                    var secret = _secretProvider.GetCachedSecret();

                    if (string.IsNullOrWhiteSpace(secret))
                        return false;

                    var x509 = cert as X509Certificate2 ??
                               (cert != null ? new X509Certificate2(cert) : null);

                    return TlsValidation.IsTrustedPeerCertificate(x509, secret, out _);
                });
        }

        private SslStream WrapClientTls(Stream raw)
        {
            return new SslStream(raw, false,
                (sender, cert, chain, errors) =>
                {
                    var secret = _secretProvider.GetCachedSecret();

                    if (string.IsNullOrWhiteSpace(secret))
                        return false;

                    var x509 = cert as X509Certificate2 ??
                               (cert != null ? new X509Certificate2(cert) : null);

                    return TlsValidation.IsTrustedPeerCertificate(x509, secret, out _);
                });
        }

        // =========================================================
        // Timeout Helper
        // =========================================================

        private static async Task WithTimeout(Task task, TimeSpan timeout, string stage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task)
                throw new TimeoutException($"Timeout during {stage} after {timeout.TotalSeconds}s");
            await task;
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string stage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task)
                throw new TimeoutException($"Timeout during {stage} after {timeout.TotalSeconds}s");
            return await task;
        }
    }
}