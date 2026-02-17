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
using ZCL.Models;
using ZCL.Repositories.Peers;

namespace ZCL.API
{
    public class Config
    {
        public static Config Instance { get; } = new Config();

        public string DBFileName { get; set; } = "services.db";
        public int DiscoveryPort { get; set; } = 2600;
        public string MulticastAddress { get; set; } = "224.0.0.26";
        public ushort ZCDPProtocolVersion { get; set; } = 0;
        public int DiscoveryTimeoutMS { get; set; } = 3 * 1000;
        public string PeerName { get; set; } = Environment.MachineName;

        private Config() { }
    }


    // Keeping it here as you asked
    public class DataStore
    {
        public ObservableCollection<PeerNode> Peers { get; } = new();
    }

    public enum MsgType
    {
        None = 0,
        Announce
    }

    public class MsgHeader
    {
        public ushort Version;
        public uint Type;
        public ulong MessageId;
        public Guid PeerGuid;
    }

    public class MsgPeerAnnounce
    {
        public required string Name;
        public required ulong ServicesCount;
    }

    public static class ZCDPPeer
    {
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

            return existing;
        }
        private static async Task<List<string>> GetOllamaModelsAsync()
        {
            try
            {
                Debug.WriteLine("Querying Ollama at http://127.0.0.1:11434/api/tags");

                using var http = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:11434"),
                    Timeout = TimeSpan.FromSeconds(5)
                };

                var response = await http.GetAsync("api/tags");

                if (!response.IsSuccessStatusCode)
                    return new List<string>();

                var json = await response.Content.ReadAsStringAsync();

                using var doc = System.Text.Json.JsonDocument.Parse(json);

                var models = new List<string>();

                foreach (var model in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameProp))
                        models.Add(nameProp.GetString() ?? "");
                }

                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ollama model discovery failed: {ex}");

                Debug.WriteLine($"Ollama model discovery failed: {ex.Message}");
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

        public static void StartAndRun(IPAddress multicastAddress, int port, string dbPath, DataStore store)
        {
            ulong MessageID = 0;
            ushort ZCDPProtocolVersion = Config.Instance.ZCDPProtocolVersion;

            // NOTE(luca): If you hardcode the same Guid on multiple machines, they will appear as ONE peer.
            // Persist a unique id per installation so each PC is discoverable.
            Guid peerGuid = GetOrCreateLocalPeerGuid(dbPath);

            Socket? sender;
            {
                try
                {
                    sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error: {e.Message}");
                    sender = null;
                }
            }

            UdpClient? listener;
            {
                try
                {
                    listener = new UdpClient();

                    listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.ExclusiveAddressUse = false;
                    listener.MulticastLoopback = false;

                    {
                        var joinedAtLeastOne = false;

                        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                            .Where(n => n.OperationalStatus == OperationalStatus.Up)
                            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                        {
                            var ipProps = ni.GetIPProperties();

                            foreach (var ua in ipProps.UnicastAddresses)
                            {
                                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                                {
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
                        }

                        if (!joinedAtLeastOne)
                        {
                            Debug.WriteLine("No IPv4 interfaces joined multicast explicitly; falling back to default join.");
                            listener.JoinMulticastGroup(multicastAddress);
                        }
                    }

                    listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                    Debug.WriteLine($"Listening for multicast on {multicastAddress}:{port}");
                    Debug.WriteLine($"Local endpoint: {listener.Client.LocalEndPoint}");
                    Debug.WriteLine($"Local PeerGuid: {peerGuid}");
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error: {e.Message}");
                    listener = null;
                }
            }

            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                try
                {
                    using var db = CreateDBContext(dbPath);

                    if (listener != null)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for data...");

                        if (listener.Available > 0)
                        {
                            byte[] bytes = listener.Receive(ref remoteEndPoint);

                            using MemoryStream memory = new MemoryStream(bytes);
                            using BinaryReader reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);

                            MsgHeader header;

                            {
                                header = new MsgHeader
                                {
                                    Version = reader.ReadUInt16(),
                                    Type = reader.ReadUInt32(),
                                    MessageId = reader.ReadUInt64()
                                };

                                Span<byte> guidBytes = stackalloc byte[16];
                                reader.Read(guidBytes);
                                header.PeerGuid = new Guid(guidBytes);
                            }

                            if (header.PeerGuid == peerGuid)
                            {
                                Debug.WriteLine(">>> Ignored self announce.");
                                goto AfterReceive;
                            }

                            Debug.WriteLine($"\n>>> Received from {remoteEndPoint} ({header.PeerGuid}):");
                            Debug.WriteLine($">>> Length: {bytes.Length} bytes");

                            switch ((MsgType)header.Type)
                            {
                                case MsgType.Announce:
                                    {
                                        MsgPeerAnnounce message = new MsgPeerAnnounce
                                        {
                                            Name = reader.ReadString(),
                                            ServicesCount = reader.ReadUInt64(),
                                        };

                                        var remoteProtocolPeerId = header.PeerGuid.ToString();
                                        var now = DateTime.UtcNow;

                                        PeerNode peer = db.PeerNodes
                                            .FirstOrDefault(p => p.ProtocolPeerId == remoteProtocolPeerId)
                                            ?? new PeerNode
                                            {
                                                PeerId = Guid.NewGuid(),
                                                ProtocolPeerId = remoteProtocolPeerId,
                                                IpAddress = remoteEndPoint.Address.ToString(),
                                                HostName = message.Name,
                                                FirstSeen = now,
                                                LastSeen = now,
                                                OnlineStatus = PeerOnlineStatus.Unknown,
                                                IsLocal = false
                                            };

                                        peer.HostName = message.Name;
                                        peer.IpAddress = remoteEndPoint.Address.ToString();
                                        peer.LastSeen = now;
                                        peer.OnlineStatus = PeerOnlineStatus.Online;

                                        if (db.Entry(peer).State == EntityState.Detached)
                                            db.PeerNodes.Add(peer);

                                        store.Peers.AddOrUpdatePeer(peer);

                                        db.SaveChanges();

                                        for (uint idx = 0; idx < message.ServicesCount; idx++)
                                        {
                                            Service service = new Service
                                            {
                                                Name = reader.ReadString(),
                                                Address = reader.ReadString(),
                                                Port = reader.ReadUInt16(),
                                                Metadata = reader.ReadString(),
                                                PeerRefId = peer.PeerId
                                            };

                                            var existing = db.Services.FirstOrDefault(s =>
                                                s.PeerRefId == peer.PeerId &&
                                                s.Name == service.Name &&
                                                s.Address == service.Address &&
                                                s.Port == service.Port);

                                            if (existing == null)
                                            {
                                                db.Services.Add(service);
                                            }
                                            else
                                            {
                                                existing.Metadata = service.Metadata;
                                            }

                                        }

                                        db.SaveChanges();
                                        break;
                                    }
                            }

                        AfterReceive:
                            ;
                        }
                        else
                        {
                            Debug.WriteLine("  (no data received, still listening...)");
                        }
                    }

                    if (sender != null)
                    {
                        Debug.WriteLine("Announcing...");

                        const ushort ZcspPort = 5555;

                        var servicesList = new List<Service>
                        {
                            new Service { Name = "FileSharing", Address = "tcp", Port = ZcspPort },
                            new Service { Name = "Messaging",  Address = "tcp", Port = ZcspPort }
                        };

                        var models = GetOllamaModelsAsync().GetAwaiter().GetResult();
                        var localIp = GetBestLocalIPv4();
                        foreach (var modelName in models)
                        {
                            servicesList.Add(new Service
                            {
                                Name = "AIChat",
                                Address = localIp,
                                Port = 5555,
                                Metadata = modelName
                            });
                        }


                        Service[] services = servicesList.ToArray();



                        MsgHeader header = new MsgHeader
                        {
                            Version = (ushort)ZCDPProtocolVersion,
                            Type = (uint)MsgType.Announce,
                            MessageId = MessageID++,
                            PeerGuid = peerGuid,
                        };

                        MsgPeerAnnounce message = new MsgPeerAnnounce
                        {
                            Name = Config.Instance.PeerName,
                            ServicesCount = (ulong)services.Length
                        };

                        using MemoryStream memory = new MemoryStream();
                        using BinaryWriter writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

                        writer.Write(header.Version);
                        writer.Write(header.Type);
                        writer.Write(header.MessageId);
                        writer.Write(header.PeerGuid.ToByteArray());

                        writer.Write(message.Name);
                        writer.Write(message.ServicesCount);

                        foreach (Service service in services)
                        {
                            writer.Write(service.Name);
                            writer.Write(service.Address);
                            writer.Write(service.Port);
                            writer.Write(service.Metadata ?? string.Empty);
                        }

                        writer.Flush();
                        sender.SendTo(memory.ToArray(), new IPEndPoint(multicastAddress, port));
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error: {e.Message}");
                }

                Thread.Sleep(Config.Instance.DiscoveryTimeoutMS);
            }
        }
    }
}
