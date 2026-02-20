using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZCL.Models;
using ZCL.Repositories.Peers;

namespace ZCL.API
{
    public sealed class Config
    {
        public static Config Instance { get; } = new();

        public string DBFileName { get; set; } = "services.db";
        public int DiscoveryPort { get; set; } = 2600;
        public string MulticastAddress { get; set; } = "224.0.0.26";
        public ushort ZCDPProtocolVersion { get; set; } = 0;
        public int DiscoveryTimeoutMS { get; set; } = 3 * 1000;
        public string PeerName { get; set; } = Environment.MachineName;
        public bool IsCoordinator { get; set; } = false;

        private Config() { }
    }

    public sealed class DataStore
    {
        public ObservableCollection<PeerNode> Peers { get; } = new();
    }

    public enum MsgType
    {
        None = 0,
        Announce
    }

    public sealed class MsgHeader
    {
        public ushort Version;
        public uint Type;
        public ulong MessageId;
        public Guid PeerGuid;
    }

    public sealed class MsgPeerAnnounce
    {
        public required string Name;
        public required ulong ServicesCount;
        public bool IsCoordinator;
    }

    public static class ZCDPPeer
    {
        private static readonly HttpClient Http = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:11434"),
            Timeout = TimeSpan.FromSeconds(5)
        };

        public static PeerNode AddOrUpdatePeer(
            this ObservableCollection<PeerNode> peers,
            PeerNode incoming)
        {
            var existing = peers.FirstOrDefault(p =>
                p.ProtocolPeerId == incoming.ProtocolPeerId);

            if (existing == null)
            {
                peers.Add(incoming);
                return incoming;
            }

            existing.HostName = incoming.HostName;
            existing.IpAddress = incoming.IpAddress;
            existing.LastSeen = incoming.LastSeen;
            existing.OnlineStatus = incoming.OnlineStatus;
            existing.IsCoordinator = incoming.IsCoordinator;

            return existing;
        }

        private static async Task<List<string>> GetOllamaModelsAsync(CancellationToken ct = default)
        {
            try
            {
                Debug.WriteLine("Querying Ollama at http://127.0.0.1:11434/api/tags");

                using var response = await Http.GetAsync("api/tags", ct);
                if (!response.IsSuccessStatusCode)
                    return new List<string>();

                var json = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();

                if (!doc.RootElement.TryGetProperty("models", out var modelsProp))
                    return models;

                foreach (var model in modelsProp.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameProp))
                        models.Add(nameProp.GetString() ?? "");
                }

                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ollama model discovery failed: {ex}");
                return new List<string>();
            }
        }

        private static string GetBestLocalIPv4()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var ip = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

                if (ip != null)
                    return ip.ToString();
            }

            return "127.0.0.1";
        }

        public static ServiceDBContext CreateDBContext(string dbPath)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ServiceDBContext>();
            optionsBuilder.UseSqlite($"Data Source={dbPath}");

            var db = new ServiceDBContext(optionsBuilder.Options);
            db.Database.EnsureCreated();

            return db;
        }

        // NOTE(luca): ZCL is a class library, so we can't use Microsoft.Maui.Storage.Preferences here.
        // Persist a stable peer guid using a small file stored next to the DB file.
        private static Guid GetOrCreateLocalPeerGuid(string dbPath)
        {
            using var db = CreateDBContext(dbPath);
            var peersRepo = new PeerRepository(db);

            var localProtocolPeerId = peersRepo
                .GetOrCreateLocalProtocolPeerIdAsync(Config.Instance.PeerName, "127.0.0.1")
                .GetAwaiter()
                .GetResult();

            return Guid.Parse(localProtocolPeerId);
        }

        private static Socket CreateSenderSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);
            return socket;
        }

        private static UdpClient CreateListener(IPAddress multicastAddress, int port)
        {
            var listener = new UdpClient();

            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.ExclusiveAddressUse = false;
            listener.MulticastLoopback = false;

            var joinedAtLeastOne = false;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var ipProps = ni.GetIPProperties();

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    try
                    {
                        listener.JoinMulticastGroup(multicastAddress, ua.Address);
                        Debug.WriteLine($"Joined multicast on {ni.Name} ({ua.Address})");
                        joinedAtLeastOne = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed join on {ni.Name} ({ua.Address}): {ex.Message}");
                    }
                }
            }

            if (!joinedAtLeastOne)
            {
                Debug.WriteLine("No IPv4 interfaces joined multicast explicitly; falling back to default join.");
                listener.JoinMulticastGroup(multicastAddress);
            }

            listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return listener;
        }

        private static async Task<Service[]> BuildAnnouncedServicesAsync(CancellationToken ct = default)
        {
            const ushort zcspPort = 5555;

            var servicesList = new List<Service>
    {
        new() { Name = "FileSharing", Address = "tcp", Port = zcspPort },
        new() { Name = "Messaging", Address = "tcp", Port = zcspPort }
    };

            var models = await GetOllamaModelsAsync(ct);

            if (models.Count > 0)
            {
                var localIp = GetBestLocalIPv4();
                var aiMetadataJson = JsonSerializer.Serialize(models);

                servicesList.Add(new Service
                {
                    Name = "LLMChat",
                    Address = localIp,
                    Port = zcspPort,
                    Metadata = aiMetadataJson
                });
            }

            // ===============================
            // NEW: Advertise Coordinator role
            // ===============================
            if (Config.Instance.IsCoordinator)
            {
                servicesList.Add(new Service
                {
                    Name = "Coordinator",
                    Address = GetBestLocalIPv4(),
                    Port = zcspPort,
                    Metadata = "routing"
                });
            }

            return servicesList.ToArray();
        }

        private static byte[] BuildAnnouncePacket(
            ushort protocolVersion,
            ulong messageId,
            Guid peerGuid,
            Service[] services)
        {
            var message = new MsgPeerAnnounce
            {
                Name = Config.Instance.PeerName,
                ServicesCount = (ulong)services.Length,
                IsCoordinator = Config.Instance.IsCoordinator
            };

            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

            writer.Write(protocolVersion);
            writer.Write((uint)MsgType.Announce);
            writer.Write(messageId);
            writer.Write(peerGuid.ToByteArray());
            writer.Write(message.Name);
            writer.Write(message.ServicesCount);
            writer.Write(message.IsCoordinator);

            foreach (var service in services)
            {
                writer.Write(service.Name);
                writer.Write(service.Address);
                writer.Write(service.Port);
                writer.Write(service.Metadata ?? string.Empty);
            }

            writer.Flush();
            return memory.ToArray();
        }

        private static async Task HandleIncomingAsync(
            byte[] bytes,
            IPEndPoint remoteEndPoint,
            Guid localPeerGuid,
            string dbPath,
            DataStore store,
            CancellationToken ct = default)
        {
            using var memory = new MemoryStream(bytes);
            using var reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);

            var header = new MsgHeader
            {
                Version = reader.ReadUInt16(),
                Type = reader.ReadUInt32(),
                MessageId = reader.ReadUInt64(),
                PeerGuid = new Guid(reader.ReadBytes(16))
            };

            if (header.PeerGuid == localPeerGuid)
                return;

            if ((MsgType)header.Type != MsgType.Announce)
                return;

            var name = reader.ReadString();
            var servicesCount = reader.ReadUInt64();
            var isCoordinator = reader.ReadBoolean();

            using var db = CreateDBContext(dbPath);

            var remoteProtocolPeerId = header.PeerGuid.ToString();
            var now = DateTime.UtcNow;

            var peer = await db.PeerNodes
                .FirstOrDefaultAsync(p => p.ProtocolPeerId == remoteProtocolPeerId, ct)
                ?? new PeerNode
                {
                    PeerId = Guid.NewGuid(),
                    ProtocolPeerId = remoteProtocolPeerId,
                    IpAddress = remoteEndPoint.Address.ToString(),
                    HostName = name,
                    FirstSeen = now,
                    LastSeen = now,
                    OnlineStatus = PeerOnlineStatus.Unknown,
                    IsLocal = false
                };

            peer.HostName = name;
            peer.IpAddress = remoteEndPoint.Address.ToString();
            peer.LastSeen = now;
            peer.OnlineStatus = PeerOnlineStatus.Online;
            peer.IsCoordinator = isCoordinator;

            if (db.Entry(peer).State == EntityState.Detached)
                db.PeerNodes.Add(peer);

            store.Peers.AddOrUpdatePeer(peer);
            await db.SaveChangesAsync(ct);

            for (ulong idx = 0; idx < servicesCount; idx++)
            {
                var service = new Service
                {
                    Name = reader.ReadString(),
                    Address = reader.ReadString(),
                    Port = reader.ReadUInt16(),
                    Metadata = reader.ReadString(),
                    PeerRefId = peer.PeerId
                };

                var existing = await db.Services
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s =>
                        s.PeerRefId == peer.PeerId &&
                        s.Name == service.Name &&
                        s.Address == service.Address &&
                        s.Port == service.Port,
                        ct);

                if (existing == null)
                {
                    db.Services.Add(service);
                }
                else
                {
                    service.ServiceId = existing.ServiceId;
                    db.Services.Update(service);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        public static async Task StartAndRunAsync(
            IPAddress multicastAddress,
            int port,
            string dbPath,
            DataStore store,
            CancellationToken ct = default)
        {
            ulong messageId = 0;
            ushort protocolVersion = Config.Instance.ZCDPProtocolVersion;

            var peerGuid = GetOrCreateLocalPeerGuid(dbPath);

            Socket? sender;
            try
            {
                sender = CreateSenderSocket();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error: {e}");
                sender = null;
            }

            UdpClient? listener;
            try
            {
                listener = CreateListener(multicastAddress, port);

                Debug.WriteLine($"Listening for multicast on {multicastAddress}:{port}");
                Debug.WriteLine($"Local endpoint: {listener.Client.LocalEndPoint}");
                Debug.WriteLine($"Local PeerGuid: {peerGuid}");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error: {e.Message}");
                listener = null;
            }

            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (listener != null && listener.Available > 0)
                    {
                        var bytes = listener.Receive(ref remoteEndPoint);
                        await HandleIncomingAsync(bytes, remoteEndPoint, peerGuid, dbPath, store, ct);
                    }

                    if (sender != null)
                    {
                        var services = await BuildAnnouncedServicesAsync(ct);
                        var payload = BuildAnnouncePacket(protocolVersion, messageId++, peerGuid, services);
                        sender.SendTo(payload, new IPEndPoint(multicastAddress, port));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error: {e.Message}");
                }

                await Task.Delay(Config.Instance.DiscoveryTimeoutMS, ct);
            }

            try { listener?.Dispose(); } catch { }
            try { sender?.Dispose(); } catch { }
        }

        public static void StartAndRun(IPAddress multicastAddress, int port, string dbPath, DataStore store)
            => StartAndRunAsync(multicastAddress, port, dbPath, store).GetAwaiter().GetResult();
    }
}
