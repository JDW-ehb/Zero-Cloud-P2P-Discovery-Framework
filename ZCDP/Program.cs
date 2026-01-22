using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Transactions;
using ZCL.Models;

namespace ZCDP
{
    internal class Program
    {

        public static int getDefaultInterface()
        {
            int result = -1;

            var defaultInterface = NetworkInterface.GetAllNetworkInterfaces()
                                                   .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                                                   .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                                   .OrderBy(ni =>
                                                   {
                                                       var props = ni.GetIPProperties();
                                                       return props.GatewayAddresses.Count == 0 ? 1 : 0;
                                                   })
                                                   .FirstOrDefault();

            if (defaultInterface != null)
            {
                var ipProps = defaultInterface.GetIPProperties();
                var ipv4Props = ipProps.GetIPv4Properties();
                result = ipv4Props.Index;
            }

            return result;
        }


        static void Main(string[] args)
        {
            int port = 2600;
            var multicastAddress = IPAddress.Parse("224.0.0.26");

            UdpClient udpClient = null;
            int defaultInterfaceIndex = getDefaultInterface();

            try
            {
                udpClient = new UdpClient();
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.ExclusiveAddressUse = false;
                udpClient.MulticastLoopback = true;
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                udpClient.JoinMulticastGroup(multicastAddress, IPAddress.Parse("192.168.178.133"));
                udpClient.MulticastLoopback = true;

                Console.WriteLine($"Listening for multicast on {multicastAddress}:{port}");
                Console.WriteLine($"Local endpoint: {udpClient.Client.LocalEndPoint}");
                Console.WriteLine("Press Ctrl+C to exit\n");

                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                while (true)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for data...");

                    udpClient.Client.ReceiveTimeout = 5000;

                    if (udpClient.Available > 0)
                    {
                        byte[] bytes = udpClient.Receive(ref remoteEP);

                        Console.WriteLine($"\n>>> Received from {remoteEP}");
                        Console.WriteLine($">>> Length: {bytes.Length} bytes");

                        using MemoryStream memory = new MemoryStream(bytes);
                        using BinaryReader reader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);

                        MsgHeader header = null;
                        Service service = null;


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
                                        service = new Service
                                        {
                                            name = Encoding.UTF8.GetString(nameBytes)
                                        };

                                        Console.WriteLine($"- {service.name}");

                                    }


                                    break;
                                }
                        }

                        Debugger.Break();
                    }
                    else
                    {
                        Console.WriteLine("  (no data received, still listening...)");
                    }

                    Thread.Sleep(3 * 1000);
                }
            }
            catch (SocketException e)
            {
                Console.WriteLine($"Socket error: {e.Message}");
                Console.WriteLine($"Error code: {e.SocketErrorCode}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
            }
            finally
            {
                udpClient?.Close();
            }
        }
    }
}