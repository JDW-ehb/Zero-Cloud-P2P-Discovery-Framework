using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ZCL.Protocol.ZCSP
{
    public interface IZcspService
    {
        string ServiceName { get; }

        Task OnSessionStartedAsync(Guid sessionId, string remotePeerId, NetworkStream stream);
        Task OnSessionDataAsync(Guid sessionId, BinaryReader reader);

        Task OnSessionClosedAsync(Guid sessionId);
    }
}
