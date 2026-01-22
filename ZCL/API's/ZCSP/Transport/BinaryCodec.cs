using System;
using System.IO;
using System.Text;

namespace ZCL.APIs.ZCSP.Protocol
{
    internal static class BinaryCodec
    {
        // =====================
        // Primitive helpers
        // =====================

        public static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            var bytes = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        // =====================
        // Message serialization
        // =====================

        public static byte[] Serialize(
            ZcspMessageType type,
            Guid? sessionId,
            Action<BinaryWriter> writePayload)
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory);

            // ---- Header ----
            writer.Write((byte)type);

            writer.Write(sessionId.HasValue);
            if (sessionId.HasValue)
                writer.Write(sessionId.Value.ToByteArray());

            writer.Write(DateTime.UtcNow.Ticks);

            // ---- Payload ----
            writePayload(writer);

            return memory.ToArray();
        }

        public static (
            ZcspMessageType Type,
            Guid? SessionId,
            DateTime Timestamp,
            BinaryReader Reader)
            Deserialize(byte[] data)
        {
            var memory = new MemoryStream(data);
            var reader = new BinaryReader(memory);

            var type = (ZcspMessageType)reader.ReadByte();

            Guid? sessionId = null;
            if (reader.ReadBoolean())
                sessionId = new Guid(reader.ReadBytes(16));

            var timestamp = new DateTime(
                reader.ReadInt64(),
                DateTimeKind.Utc);

            return (type, sessionId, timestamp, reader);
        }
    }
}
