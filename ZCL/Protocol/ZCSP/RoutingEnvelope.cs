using System;
using System.IO;

namespace ZCL.Protocol.ZCSP.Protocol
{
    /// <summary>
    /// Wraps an inner SessionData payload so it can be routed via a server.
    /// The server forwards it to the destination peer.
    /// </summary>
    internal static class RoutingEnvelope
    {
        // Write envelope fields in a strict order.
        public static void Write(
            BinaryWriter w,
            Guid routeId,
            string fromPeerId,
            string toPeerId,
            string serviceName,
            byte[] innerPayload)
        {
            w.Write(routeId.ToByteArray());

            BinaryCodec.WriteString(w, fromPeerId);
            BinaryCodec.WriteString(w, toPeerId);
            BinaryCodec.WriteString(w, serviceName);

            w.Write(innerPayload.Length);
            w.Write(innerPayload);
        }

        public static (
            Guid RouteId,
            string FromPeerId,
            string ToPeerId,
            string ServiceName,
            byte[] InnerPayload)
            Read(BinaryReader r)
        {
            var routeId = new Guid(r.ReadBytes(16));

            var fromPeerId = BinaryCodec.ReadString(r);
            var toPeerId = BinaryCodec.ReadString(r);
            var serviceName = BinaryCodec.ReadString(r);

            var len = r.ReadInt32();
            var inner = r.ReadBytes(len);

            return (routeId, fromPeerId, toPeerId, serviceName, inner);
        }
    }
}
