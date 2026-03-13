using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        private readonly ZCL.Security.TrustGroupCache _trustGroups;
        private CancellationTokenSource? _hostingCts;
        private Task? _hostingTask;
        public string PeerId => _peerId ?? "(unresolved)";

        public ZcspPeer(
            IServiceScopeFactory scopeFactory,
            SessionRegistry sessions,
            RoutingState routing,
            ZCL.Security.TrustGroupCache trustGroups)
        {
            _scopeFactory = scopeFactory;
            _sessions = sessions;
            _routing = routing;
            _trustGroups = trustGroups;
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
                throw new InvalidOperationException(
                    $"Local protocol peer id must be a GUID string, got '{_peerId}'.");

            return _peerId!;
        }

        public void StartHosting(int port, Func<string, IZcspService?> serviceResolver)
        {
            if (_hostingTask != null)
                return;

            _hostingCts = new CancellationTokenSource();

            _hostingTask = Task.Run(() =>
                StartHostingLoopAsync(port, serviceResolver, _hostingCts.Token));
        }

        private async Task StartHostingLoopAsync(
            int port,
            Func<string, IZcspService?> serviceResolver,
            CancellationToken ct)
        {
            var localId = await EnsurePeerIdAsync();

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            Console.WriteLine($"[{localId}] Hosting on port {port}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!listener.Pending())
                    {
                        await Task.Delay(50, ct);
                        continue;
                    }

                    var client = await listener.AcceptTcpClientAsync(ct);

                    _ = Task.Run(async () =>
                    {
                        try { await HandleClientAsync(client, serviceResolver); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{localId}] Client handler crashed:");
                            Console.WriteLine(ex);
                        }
                    }, ct);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                listener.Stop();
            }
        }

        public async Task StopHostingAsync()
        {
            if (_hostingCts == null)
                return;

            _hostingCts.Cancel();

            try
            {
                if (_hostingTask != null)
                    await _hostingTask;
            }
            catch { }

            _hostingTask = null;
            _hostingCts.Dispose();
            _hostingCts = null;
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

                        if (!await PerformGroupAuthorizationAsync(tls))
                        {
                            client.Close();
                            return;
                        }

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

                        session.AttachTransport(tls);

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

        public async Task ConnectAsync(
    string host,
    int port,
    string remotePeerId,
    IZcspService service,
    CancellationToken ct = default)
        {
            var localId = await EnsurePeerIdAsync(ct);

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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            TcpClient? client = null;
            NetworkStream? raw = null;
            SslStream? tls = null;

            try
            {
                client = new TcpClient
                {
                    NoDelay = true
                };

                Console.WriteLine($"[CONNECT] Mode={_routing.Mode} Connecting to {connectHost}:{connectPort} (finalTo={finalToPeerId})");

                await client.ConnectAsync(connectHost, connectPort).WaitAsync(timeoutCts.Token);

                raw = client.GetStream();

                var myCert = LoadLocalTlsIdentity();

                tls = WrapClientTls(raw);

                var clientOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = connectHost,
                    ClientCertificates = new X509CertificateCollection { myCert },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };

                await tls.AuthenticateAsClientAsync(clientOptions, timeoutCts.Token);

                await SendGroupAuthAsync(tls, myCert);

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

                var (type, sessionId, _, reader) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.ServiceResponse || sessionId == null)
                    throw new InvalidOperationException("Invalid service response.");

                var ok = reader.ReadBoolean();
                var expiresTicks = reader.ReadInt64();

                if (!ok)
                    throw new UnauthorizedAccessException("Remote rejected service request.");

                var expiresAtUtc = new DateTime(expiresTicks, DateTimeKind.Utc);

                var session = _sessions.AddExisting(sessionId.Value, finalToPeerId, expiresAtUtc);

                session.AttachTransport(tls);

                await service.OnSessionStartedAsync(sessionId.Value, finalToPeerId, tls);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunSessionAsync(tls, sessionId.Value, service);
                    }
                    catch {  }
                });
            }
            catch
            {
                try { tls?.Dispose(); } catch { }
                try { raw?.Dispose(); } catch { }
                try { client?.Dispose(); } catch { }
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

                    try { frame = await Framing.ReadAsync(stream); }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }

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
                try { await service.OnSessionClosedAsync(sessionId); } catch { }
                _sessions.Remove(sessionId);
            }
        }

        private X509Certificate2 LoadLocalTlsIdentity()
        {
            var baseDir = Config.Instance.AppDataDirectory;
            var pfxPath = Path.Combine(baseDir, TlsConstants.DefaultPfxFileName);

            if (File.Exists(pfxPath))
            {
                try
                {
                    var loaded = new X509Certificate2(
                        pfxPath,
                        TlsConstants.DefaultPfxPassword,
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                    if (loaded.HasPrivateKey && ValidateNetworkProof(loaded))
                        return loaded;

                    loaded.Dispose();
                }
                catch
                {
                }

                try { File.Delete(pfxPath); } catch { }
            }

            var networkSecretBytes = SHA256.HashData(
                Encoding.UTF8.GetBytes(Config.Instance.NetworkSecret));

            var created = TlsCertificateProvider.CreateNetworkIdentityCertificate(
                peerLabel: Config.Instance.PeerName,
                networkSecret: networkSecretBytes);

            TlsCertificateProvider.SavePfx(
                created,
                pfxPath,
                TlsConstants.DefaultPfxPassword);

            return created;
        }

        private SslStream WrapServerTls(Stream raw)
        {
            return new SslStream(
                raw,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, cert, chain, errors) =>
                {
                    var x509 = cert as X509Certificate2 ??
                               (cert != null ? new X509Certificate2(cert) : null);

                    if (x509 == null)
                        return false;

                    return ValidateNetworkProof(x509);
                });
        }

        private SslStream WrapClientTls(Stream raw)
        {
            return new SslStream(
                raw,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, cert, chain, errors) =>
                {
                    var x509 = cert as X509Certificate2 ??
                               (cert != null ? new X509Certificate2(cert) : null);

                    if (x509 == null)
                        return false;

                    return ValidateNetworkProof(x509);
                });
        }

        private bool ValidateNetworkProof(X509Certificate2 cert)
        {
            var ext = cert.Extensions
                .OfType<X509Extension>()
                .FirstOrDefault(e => e.Oid?.Value == TlsConstants.NetworkProofOid);

            if (ext == null)
                return false;

            string? remoteHex;

            try { remoteHex = Encoding.UTF8.GetString(ext.RawData)?.Trim(); }
            catch { return false; }

            if (string.IsNullOrWhiteSpace(remoteHex))
                return false;

            var networkSecretBytes = SHA256.HashData(
                Encoding.UTF8.GetBytes(Config.Instance.NetworkSecret));

            var expected = new HMACSHA256(networkSecretBytes)
                .ComputeHash(cert.PublicKey.EncodedKeyValue.RawData);

            var expectedHex = Convert.ToHexString(expected);

            return ConstantTimeEquals(remoteHex, expectedHex);
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];

            return diff == 0;
        }

        // ===========================
        // GROUP AUTH (FIXED)
        // - server does NOT rely on GUID
        // - it matches by proof against enabled secrets
        // ===========================

        private async Task<bool> PerformGroupAuthorizationAsync(SslStream tls)
        {
            Console.WriteLine("[GROUP AUTH] Waiting for group auth request(s)...");

            var remoteCert = tls.RemoteCertificate as X509Certificate2
                ?? (tls.RemoteCertificate != null ? new X509Certificate2(tls.RemoteCertificate) : null);

            if (remoteCert == null)
            {
                Console.WriteLine("[GROUP AUTH] No remote certificate.");
                return false;
            }

            var enabledGroups = GetEnabledGroups();
            if (enabledGroups.Count == 0)
            {
                Console.WriteLine("[GROUP AUTH] No enabled trust groups locally.");
                return false;
            }

            // allow multiple attempts (client can try multiple groups)
            // cap attempts so we don't hang forever if a client is malicious/broken
            var maxAttempts = Math.Min(enabledGroups.Count, 16);

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var frame = await Framing.ReadAsync(tls);
                if (frame == null)
                {
                    Console.WriteLine("[GROUP AUTH] Connection closed while waiting.");
                    return false;
                }

                var (type, _, _, reader) = BinaryCodec.Deserialize(frame);
                if (type != ZcspMessageType.GroupAuthRequest)
                {
                    Console.WriteLine($"[GROUP AUTH] Unexpected message type: {type}");
                    return false;
                }

                // protocol still sends a GUID, but we DO NOT trust/use it for matching anymore
                _ = reader.ReadBytes(16); // ignore groupId
                var proofHex = BinaryCodec.ReadString(reader);

                if (string.IsNullOrWhiteSpace(proofHex))
                {
                    Console.WriteLine("[GROUP AUTH] Empty proof.");
                    await SendGroupAuthResponseAsync(tls, ok: false);
                    continue;
                }

                var matched = false;

                foreach (var group in enabledGroups)
                {
                    if (!TryReadGroupSecret(group.SecretHex, out var secretBytes))
                        continue;

                    var expected = ComputeGroupProof(secretBytes, remoteCert);
                    var expectedHex = Convert.ToHexString(expected);

                    if (ConstantTimeEquals(proofHex, expectedHex))
                    {
                        matched = true;
                        Console.WriteLine($"[GROUP AUTH] SUCCESS via group '{group.Name}'.");
                        break;
                    }
                }

                await SendGroupAuthResponseAsync(tls, ok: matched);

                if (matched)
                    return true;

                Console.WriteLine("[GROUP AUTH] Proof mismatch (no enabled group matched).");
            }

            Console.WriteLine("[GROUP AUTH] Failed after max attempts.");
            return false;
        }

        private static Task SendGroupAuthResponseAsync(SslStream tls, bool ok)
        {
            var response = BinaryCodec.Serialize(
                ZcspMessageType.GroupAuthResponse,
                null,
                w => w.Write(ok));

            return Framing.WriteAsync(tls, response);
        }

        private List<TrustGroupEntity> GetEnabledGroups()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            return db.TrustGroups.Where(x => x.IsEnabled).ToList();
        }

        private static byte[] ComputeGroupProof(byte[] secret, X509Certificate2 cert)
        {
            return new HMACSHA256(secret)
                .ComputeHash(cert.PublicKey.EncodedKeyValue.RawData);
        }

        private async Task SendGroupAuthAsync(SslStream tls, X509Certificate2 myCert)
        {
            var activeGroups = GetEnabledGroups();
            Console.WriteLine($"[GROUP AUTH CLIENT] Trying {activeGroups.Count} enabled groups.");

            foreach (var group in activeGroups)
            {
                Console.WriteLine($"[GROUP AUTH CLIENT] Trying group: {group.Name}");

                if (!TryReadGroupSecret(group.SecretHex, out var secretBytes))
                    continue;

                var proof = ComputeGroupProof(secretBytes, myCert);
                var proofHex = Convert.ToHexString(proof);

                var frame = BinaryCodec.Serialize(
                    ZcspMessageType.GroupAuthRequest,
                    null,
                    w =>
                    {
                        // keep field for backward compatibility; server ignores it now
                        w.Write(group.Id.ToByteArray());
                        BinaryCodec.WriteString(w, proofHex);
                    });

                await Framing.WriteAsync(tls, frame);

                var response = await Framing.ReadAsync(tls);
                if (response == null)
                {
                    Console.WriteLine("[GROUP AUTH CLIENT] No response.");
                    continue;
                }

                var (type, _, _, reader) = BinaryCodec.Deserialize(response);
                if (type != ZcspMessageType.GroupAuthResponse)
                {
                    Console.WriteLine("[GROUP AUTH CLIENT] Unexpected response type.");
                    continue;
                }

                var ok = reader.ReadBoolean();
                Console.WriteLine($"[GROUP AUTH CLIENT] Server response: {ok}");

                if (ok)
                {
                    Console.WriteLine("[GROUP AUTH CLIENT] SUCCESS.");
                    return;
                }
            }

            Console.WriteLine("[GROUP AUTH CLIENT] No shared trust group found.");
            throw new UnauthorizedAccessException("No shared trust group.");
        }

        private static bool TryReadGroupSecret(string? secretHex, out byte[] secretBytes)
        {
            secretBytes = Array.Empty<byte>();

            if (string.IsNullOrWhiteSpace(secretHex))
                return false;

            try
            {
                secretBytes = Convert.FromHexString(secretHex.Trim());
                return secretBytes.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}