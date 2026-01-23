using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;

using ZCL.Models;

namespace ZCL.API
{
    public static class Config
    {
        public const string dbFileName = "services.db";
        public const int port = 2600;
        public const string multicastAddressString = "224.0.0.26";
        public const UInt16 ZCDPProtocolVersion = 0;
    }

    public enum MsgType
    {
        None = 0,
        Announce
    }

    public class MsgHeader
    {
        public UInt16 version;
        public UInt32 type;
        public UInt64 messageID;
        public Guid peerGuid;
    }

    public class MsgAnnounce
    {
        public UInt64 servicesCount;
    }

    public static class ZCDPPeer
    {
        public static void StartAndRun(IPAddress multicastAddress, int port, string dbPath)
        {
            UInt64 MessageID = 0;
            UInt16 ZCDPProtocolVersion = Config.ZCDPProtocolVersion;
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
                                if (unicastAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
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


            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                try
                {
                    var optionsBuilder = new DbContextOptionsBuilder<ServiceDBContext>();
                    optionsBuilder.UseSqlite($"Data Source={dbPath}");
                    var db = new ServiceDBContext(optionsBuilder.Options);
                    
                    if (listener != null)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for data...");

                        if (listener.Available > 0)
                        {
                            byte[] bytes = listener.Receive(ref remoteEP);

                            // TODO(luca): Bulletproof reading of packets since they might be corrupted or wrong.
                            // Create a wrapper that will exit and log if reading fails.

                            using MemoryStream memory = new MemoryStream(bytes);
                            using BinaryReader reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);

                            MsgHeader header;

                            // Decode header
                            {
                                header = new MsgHeader
                                {
                                    version = reader.ReadUInt16(),
                                    type = reader.ReadUInt32(),
                                    messageID = reader.ReadUInt64()
                                };

                                Span<byte> guidBytes = stackalloc byte[16];
                                reader.Read(guidBytes);
                                header.peerGuid = new Guid(guidBytes);
                            }


                            Debug.WriteLine($"\n>>> Received from {remoteEP} ({header.peerGuid}):");
                            Debug.WriteLine($">>> Length: {bytes.Length} bytes");

                            switch ((MsgType)header.type)
                            {
                                case MsgType.Announce:
                                    {
                                        MsgAnnounce message = new MsgAnnounce
                                        {
                                            servicesCount = reader.ReadUInt64()
                                        };

                                        for (UInt32 idx = 0; idx < message.servicesCount; idx += 1)
                                        {
                                            if (idx == 0)
                                            {
                                                Debug.WriteLine("Services:");
                                            }

                                            Service service = new Service
                                            {
                                                name = reader.ReadString(),
                                                address = reader.ReadString(),
                                                port = reader.ReadUInt16(),
                                                peerGuid = header.peerGuid,
                                            };

                                            db.Add(service);

                                            Debug.WriteLine($"- {service.name}");
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
                               new Service { name = "FileTransfer", address = "1.1.1.1", port = 1111 },
                               new Service { name = "Messaging", address = "2.2.2.2", port = 2222 },
                               new Service { name = "AIChat", address = "3.3.3.3", port = 3333 },
                        ];

                        MsgHeader header = new MsgHeader
                        {
                            version = (UInt16)ZCDPProtocolVersion,
                            type = (UInt32)MsgType.Announce,
                            messageID = MessageID,
                            peerGuid = peerGuid,
                        };

                        MsgAnnounce message = new MsgAnnounce
                        {
                            servicesCount = (UInt64)services.Length
                        };

                        using MemoryStream memory = new MemoryStream();
                        using BinaryWriter writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

                        writer.Write(header.version);
                        writer.Write(header.type);
                        writer.Write(header.messageID);
                        writer.Write(header.peerGuid.ToByteArray());

                        writer.Write(message.servicesCount);

                        foreach (Service service in services)
                        {
                            writer.Write(service.name);
                            writer.Write(service.address);
                            writer.Write(service.port);
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

                Thread.Sleep(3 * 1000);

            }
        }
    }

}