using System.IO;
using System.Net.Sockets;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;

namespace ZCL.Services.FileSharing;

public sealed class FileSharingSessionHandler : IZcspService
{
    public string ServiceName => "FileSharing";

    private readonly FileSharingService _hub;
    private NetworkStream? _stream;

    public FileSharingSessionHandler(FileSharingService hub)
        => _hub = hub;

    public void BindStream(NetworkStream stream)
        => _stream = stream;

    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
        => _hub.InternalOnSessionStartedAsync(sessionId, remotePeerId, _stream!);

    public Task OnSessionClosedAsync(Guid sessionId)
        => _hub.InternalOnSessionClosedAsync(sessionId);

    public Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
        => _hub.InternalOnSessionDataAsync(sessionId, reader);
}
