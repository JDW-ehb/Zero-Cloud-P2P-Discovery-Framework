using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Transactions;
using ZCL.API_s;

namespace ZCDP
{
    internal class Program
    {

        public static UdpClient CreateServer(IPAddress multicastAddress, int port)
        {
            UdpClient result = new UdpClient();

            result.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            result.ExclusiveAddressUse = false;
            // NOTE(luca): We should not receive messages from ourself.
            result.MulticastLoopback = false;

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

            byte[] outValue = BitConverter.GetBytes(16);

            if (defaultInterfaceAddress != null)
            {
                result.JoinMulticastGroup(multicastAddress, defaultInterfaceAddress);
            }
            else
            {
                // TODO(luca): Log this.
                result.JoinMulticastGroup(multicastAddress);
            }

            result.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            return result;
        }

        static void Main(string[] args)
        {
            int port = 2600;
            var multicastAddress = IPAddress.Parse("224.0.0.26");
            UInt64 MessageID = 0;
            UInt16 ZCDPProtocolVersion = 0;

            UdpClient? udpClient = null;
            try
            {
                udpClient = CreateServer(multicastAddress, port);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
            }

            if (udpClient != null)
            {
                Console.WriteLine($"Listening for multicast on {multicastAddress}:{port}");
                Console.WriteLine($"Local endpoint: {udpClient.Client.LocalEndPoint}");
                Console.WriteLine("Press Ctrl+C to exit\n");

                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                while (true)
                {
                    try
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for data...");

                        if (udpClient.Available > 0)
                        {
                            byte[] bytes = udpClient.Receive(ref remoteEP);

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
                            }

                        }
                        else
                        {
                            Console.WriteLine("  (no data received, still listening...)");
                        }

                        // Announce services to other peers
                        {
                            Console.WriteLine("Announcing...");
                            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);

                            Service[] services =
                            {
                               new Service { name = "FileTransfer" },
                               new Service { name = "Messaging" },
                               new Service { name = "AIChat" },
                        };


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
                            socket.SendTo(packet, endPoint);

                            socket.Close();
                        }

                        MessageID += 1;

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
}
