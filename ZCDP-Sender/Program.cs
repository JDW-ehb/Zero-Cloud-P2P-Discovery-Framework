using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

using ZCL.Models;

namespace ZCDP_Sender
{
    internal class Program
    {
        static void Main(string[] args)
        {
            IPAddress multicastAddress = IPAddress.Parse("224.0.0.26");
            int port = 2600;

            Console.WriteLine("Announcing...");
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);

            MsgHeader header = new MsgHeader
            {
                version = 3,
                type = (UInt32)MsgType.Announce,
                messageID = 37,
                peerUUID = UInt128.Parse("12345678901234567890123456789012")
            };

            MsgAnnounce message = new MsgAnnounce
            {
                servicesCount = 1
            };

            Service service = new Service
            {
                name = "DummyService"
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

            byte[] nameBytes = Encoding.UTF8.GetBytes(service.name);
            writer.Write((UInt64)nameBytes.Length);
            writer.Write(nameBytes);

            writer.Flush();

            byte[] packet = memory.ToArray();
        
            IPEndPoint endPoint = new IPEndPoint(multicastAddress, port);
            socket.SendTo(packet, endPoint);

            socket.Close();

        }
    }
}
