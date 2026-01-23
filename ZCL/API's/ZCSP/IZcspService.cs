using System;
using System.IO;
using System.Threading.Tasks;

namespace ZCL.APIs.ZCSP
{
    public interface IZcspService
    {
        string ServiceName { get; }

        Task OnSessionStartedAsync(Guid sessionId, string remotePeerId);

        Task OnSessionDataAsync(Guid sessionId, BinaryReader reader);

        Task OnSessionClosedAsync(Guid sessionId);
    }
}
