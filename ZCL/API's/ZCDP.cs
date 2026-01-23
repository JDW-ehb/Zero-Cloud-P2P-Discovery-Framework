using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ZCL.API_s
{
    public enum MsgType
    {
        None = 0,
        Announce
    }

    public class Service
    {
        UInt16 port;
        public required string name;
    }

    public class MsgHeader
    {
        public UInt16 version;
        public UInt32 type;
        public UInt64 messageID;
        public UInt128 peerUUID;
    }

    public class MsgAnnounce
    {
        public UInt64 servicesCount;
    }

    public class ZCDP
    {
        public static void StartAndRunPeer(IPAddress multicastAddress, int port)
        {
            UInt64 MessageID = 0;
            UInt16 ZCDPProtocolVersion = 0;

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
                    Console.WriteLine($"Error: {e.Message}");
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

                    Console.WriteLine($"Listening for multicast on {multicastAddress}:{port}");
                    Console.WriteLine($"Local endpoint: {listener.Client.LocalEndPoint}");

                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: {e.Message}");
                    listener = null;
                }
            }


            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                try
                {
                    if (listener != null)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for data...");

                        if (listener.Available > 0)
                        {
                            byte[] bytes = listener.Receive(ref remoteEP);

                            Console.WriteLine($"\n>>> Received from {remoteEP}");
                            Console.WriteLine($">>> Length: {bytes.Length} bytes");

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

                                UInt64 upper = reader.ReadUInt64();
                                UInt64 lower = reader.ReadUInt64();
                                header.peerUUID = ((UInt128)(lower) | ((UInt128)(upper) << 64));
                            }

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
                                                Console.WriteLine("Services:");
                                            }

                                            UInt64 nameLength = reader.ReadUInt64();
                                            byte[] nameBytes = reader.ReadBytes((int)nameLength);
                                            Service service = new Service
                                            {
                                                name = Encoding.UTF8.GetString(nameBytes)
                                            };

                                            Console.WriteLine($"- {service.name}");
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
                            Console.WriteLine("  (no data received, still listening...)");
                        }
                    }

                    // Announce services to other peers
                    if (sender != null)
                    {
                        Console.WriteLine("Announcing...");

                        Service[] services =
                        [
                               new Service { name = "FileTransfer" },
                               new Service { name = "Messaging" },
                               new Service { name = "AIChat" },
                        ];

                        MsgHeader header = new MsgHeader
                        {
                            version = (UInt16)ZCDPProtocolVersion,
                            type = (UInt32)MsgType.Announce,
                            messageID = MessageID,
                            peerUUID = UInt128.Parse("12345678901234567890123456789012")
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

                        UInt64 lower = (UInt64)(header.peerUUID);
                        UInt64 upper = (UInt64)(header.peerUUID >> 64);
                        writer.Write(upper);
                        writer.Write(lower);

                        writer.Write(message.servicesCount);

                        foreach (Service service in services)
                        {
                            byte[] nameBytes = Encoding.UTF8.GetBytes(service.name);
                            writer.Write((UInt64)nameBytes.Length);
                            writer.Write(nameBytes);
                        }

                        writer.Flush();

                        byte[] packet = memory.ToArray();

                        IPEndPoint endPoint = new IPEndPoint(multicastAddress, port);
                        sender.SendTo(packet, endPoint);

                        MessageID += 1;

                    }

                    Thread.Sleep(3 * 1000);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: {e.Message}");
                }

            }
        }
    }

}