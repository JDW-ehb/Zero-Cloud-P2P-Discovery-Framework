using System.IO;
using System.Net.Sockets;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Services.Messaging;

public sealed class MessagingSessionHandler : IZcspService
{
    public string ServiceName => "Messaging";

    private readonly MessagingService _hub;   // singleton “hub”
    private NetworkStream? _stream;

    public MessagingSessionHandler(MessagingService hub)
        => _hub = hub;

    public void BindStream(NetworkStream stream)
        => _stream = stream;

    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
        => _hub.InternalOnSessionStartedAsync(sessionId, remotePeerId, _stream!);

    public Task OnSessionClosedAsync(Guid sessionId)
        => _hub.InternalOnSessionClosedAsync(sessionId);

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        // Same decode format you already use
        var fromPeer = BinaryCodec.ReadString(reader);
        var toPeer = BinaryCodec.ReadString(reader);
        var content = BinaryCodec.ReadString(reader);

        await _hub.InternalOnIncomingAsync(sessionId, fromPeer, toPeer, content);
    }
}
