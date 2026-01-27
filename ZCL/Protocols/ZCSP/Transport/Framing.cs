using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ZCL.APIs.ZCSP.Transport
{
    internal static class Framing
    {
        /// <summary>
        /// Writes a length-prefixed frame to the stream.
        /// Frame format: [4-byte length][payload bytes]
        /// </summary>
        public static async Task WriteAsync(NetworkStream stream, byte[] payload)
        {
            var lengthPrefix = BitConverter.GetBytes(payload.Length);

            await stream.WriteAsync(lengthPrefix);
            await stream.WriteAsync(payload);
        }

        /// <summary>
        /// Reads a full length-prefixed frame from the stream.
        /// Returns null if the connection is closed.
        /// </summary>
        public static async Task<byte[]?> ReadAsync(NetworkStream stream)
        {
            var lengthBuffer = new byte[4];

            if (await ReadExactAsync(stream, lengthBuffer) == 0)
                return null;

            int payloadLength = BitConverter.ToInt32(lengthBuffer);
            var payload = new byte[payloadLength];

            await ReadExactAsync(stream, payload);
            return payload;
        }

        /// <summary>
        /// Reads exactly buffer.Length bytes unless the connection closes.
        /// </summary>
        private static async Task<int> ReadExactAsync(
            NetworkStream stream,
            byte[] buffer)
        {
            int offset = 0;

            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset));
                if (read == 0)
                    return 0;

                offset += read;
            }

            return offset;
        }
    }
}
