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

namespace ZCL.API
{
    public static class Config
    {
        public const string DBFileName = "services.db";
        public const int Port = 2600;
        public const string MulticastAddress = "224.0.0.26";
        public const ushort ZCDPProtocolVersion = 0;
        public const int DiscoveryTimeoutMS = (3 * 1000);

        // TODO(luca): We should really use the computer's name instead.
        public const string peerName = "Luca's desktop";
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
        public static PeerNode? AddUniquePeer(this ObservableCollection<PeerNode> peers, PeerNode peer)
        {
            // NOTE: PeerNode doesn't have Name/Address/Guid like the old Peer model.
            // ProtocolPeerId is the stable identity in your new model, so use that.
            var found = peers.FirstOrDefault(p => p.ProtocolPeerId == peer.ProtocolPeerId);

            if (found == null)
            {
                peers.Add(peer);
            }

            return found;
        }

        public static ServiceDBContext CreateDBContext(string dbPath)
        {
            ServiceDBContext result;

            var optionsBuilder = new DbContextOptionsBuilder<ServiceDBContext>();
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
            result = new ServiceDBContext(optionsBuilder.Options);

            result.Database.EnsureCreated();

            return result;
        }

        public static void StartAndRun(IPAddress multicastAddress, int port, string dbPath, DataStore store)
        {
            ulong MessageID = 0;
            ushort ZCDPProtocolVersion = Config.ZCDPProtocolVersion;
            Guid peerGuid = Guid.Parse("2c0dbe0c-91c4-46cf-92e4-cf43a614a914");

            Socket? sender;
            // Create sender
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
            // Create listener
            {
                try
                {
                    listener = new UdpClient();

                    listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.ExclusiveAddressUse = false;
                    // NOTE(luca): We should not receive messages from ourself.
                    listener.MulticastLoopback = false;

                    IPAddress? defaultInterfaceAddress = null;
                    // NOTE(luca): The default interface which we bind to might be wrong, therefore we must
                    // find the correct one i.e., the one used for the computer to access remote networks.
                    {
                        var defaultInterface = NetworkInterface.GetAllNetworkInterfaces()
                            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                            .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                            .OrderBy(networkInterface =>
                            {
                                var props = networkInterface.GetIPProperties();
                                return ((props.GatewayAddresses.Count == 0) ? 1 : 0);
                            })
                            .FirstOrDefault();

                        if (defaultInterface != null)
                        {
                            var ipProps = defaultInterface.GetIPProperties();
                            foreach (UnicastIPAddressInformation unicastAddress in ipProps.UnicastAddresses)
                            {
                                if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    defaultInterfaceAddress = unicastAddress.Address;
                                }
                            }
                        }
                    }

                    if (defaultInterfaceAddress != null)
                    {
                        listener.JoinMulticastGroup(multicastAddress, defaultInterfaceAddress);
                    }
                    else
                    {
                        // TODO(luca): Log this.
                        listener.JoinMulticastGroup(multicastAddress);
                    }

                    listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                    Debug.WriteLine($"Listening for multicast on {multicastAddress}:{port}");
                    Debug.WriteLine($"Local endpoint: {listener.Client.LocalEndPoint}");
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
                    //  db.ChangeTracker.Clear();

                    if (listener != null)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for data...");

                        if (listener.Available > 0)
                        {
                            byte[] bytes = listener.Receive(ref remoteEndPoint);

                            // TODO(luca): Bulletproof reading of packets since they might be corrupted or wrong.
                            // Create a wrapper that will exit and log if reading fails.

                            using MemoryStream memory = new MemoryStream(bytes);
                            using BinaryReader reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);

                            MsgHeader header;

                            // Decode header
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

                                        // NOTE(luca): Sometimes the address of a peer might change, we should detect based on the Guid.
                                        // PeerNode doesn't store Guid directly; we use ProtocolPeerId = Guid.ToString() as identity.
                                        var remoteProtocolPeerId = header.PeerGuid.ToString();
                                        var now = DateTime.UtcNow;

                                        // DbSet must be PeerNodes in your context
                                        PeerNode? peer = db.PeerNodes
                                            .FirstOrDefault(p => p.ProtocolPeerId == remoteProtocolPeerId);

                                        if (peer != null)
                                        {
                                            // message.Name becomes HostName in PeerNode
                                            peer.HostName = message.Name;

                                            // Address becomes IpAddress in PeerNode
                                            peer.IpAddress = remoteEndPoint.Address.ToString();

                                            peer.LastSeen = now;
                                            peer.OnlineStatus = PeerOnlineStatus.Online;

                                            var memoryPeer = store.Peers
                                                .FirstOrDefault(p => p.ProtocolPeerId == remoteProtocolPeerId);

                                            if (memoryPeer != null)
                                            {
                                                memoryPeer.HostName = peer.HostName;
                                                memoryPeer.IpAddress = peer.IpAddress;
                                                memoryPeer.LastSeen = now;
                                                memoryPeer.OnlineStatus = peer.OnlineStatus;
                                            }
                                        }
                                        else
                                        {
                                            peer = new PeerNode
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

                                            db.PeerNodes.Add(peer);
                                            store.Peers.Add(peer);
                                        }

                                        db.SaveChanges();

                                        for (uint idx = 0; idx < message.ServicesCount; idx += 1)
                                        {
                                            if (idx == 0)
                                            {
                                                Debug.WriteLine("Services:");
                                            }

                                            // Service property casing + link to peer via PeerRefId
                                            Service service = new Service
                                            {
                                                Name = reader.ReadString(),
                                                Address = reader.ReadString(),
                                                Port = reader.ReadUInt16(),
                                                PeerRefId = peer.PeerId
                                            };

                                            // TODO(luca): This is really annoying, what I would want is that the dbContext does not fail when
                                            // adding a new service because that will break the updates above as well.

                                            // Add only if it is unique
                                            {
                                                bool exists = db.Services.Any(s =>
                                                    s.PeerRefId == peer.PeerId &&
                                                    s.Name == service.Name &&
                                                    s.Address == service.Address &&
                                                    s.Port == service.Port);

                                                if (!exists)
                                                {
                                                    db.Services.Add(service);
                                                    Debug.WriteLine($"- Added service {service.Name}");
                                                }
                                            }
                                        }

                                        try
                                        {
                                            db.SaveChanges();
                                        }
                                        catch (Exception e)
                                        {
                                            Debug.WriteLine($"Could not save changes: {e.Message}");
                                            Debug.WriteLine(e.InnerException?.Message);
                                        }

                                        break;
                                    }
                                default:
                                    {
                                        // TODO(luca): Log unhandled message.
                                        break;
                                    }
                            }
                        }
                        else
                        {
                            Debug.WriteLine("  (no data received, still listening...)");
                        }
                    }

                    // Announce services to other peers
                    if (sender != null)
                    {
                        Debug.WriteLine("Announcing...");

                        Service[] services =
                        [
                            new Service { Name = "FileTransfer", Address = "1.1.1.1", Port = 1111 },
                            new Service { Name = "Messaging", Address = "2.2.2.2", Port = 2222 },
                            new Service { Name = "AIChat", Address = "3.3.3.3", Port = 3333 },
                        ];

                        MsgHeader header = new MsgHeader
                        {
                            Version = (ushort)ZCDPProtocolVersion,
                            Type = (uint)MsgType.Announce,
                            MessageId = MessageID,
                            PeerGuid = peerGuid,
                        };

                        MsgPeerAnnounce message = new MsgPeerAnnounce
                        {
                            Name = Config.peerName,
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
                        }

                        writer.Flush();

                        byte[] packet = memory.ToArray();

                        IPEndPoint endPoint = new IPEndPoint(multicastAddress, port);
                        sender.SendTo(packet, endPoint);

                        MessageID += 1;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Error: {e.Message}");
                }

                Thread.Sleep(Config.DiscoveryTimeoutMS);
            }
        }
    }
}
