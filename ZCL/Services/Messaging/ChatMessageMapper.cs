using ZCL.Models;

namespace ZCL.Services.Messaging;

public static class ChatMessageMapper
{
    public static ChatMessage Outgoing(
        string localProtocolPeerId,
        string remoteProtocolPeerId,
        MessageEntity entity)
    {
        return new ChatMessage(
            fromPeer: localProtocolPeerId,
            toPeer: remoteProtocolPeerId,
            content: entity.Content,
            direction: MessageDirection.Outgoing,
            timestamp: entity.Timestamp
        );
    }

    public static ChatMessage Incoming(
        string fromProtocolPeerId,
        string toProtocolPeerId,
        MessageEntity entity)
    {
        return new ChatMessage(
            fromPeer: fromProtocolPeerId,
            toPeer: toProtocolPeerId,
            content: entity.Content,
            direction: MessageDirection.Incoming,
            timestamp: entity.Timestamp
        );
    }

    public static ChatMessage FromHistory(
        MessageEntity entity,
        string localProtocolPeerId,
        string remoteProtocolPeerId)
    {
        var isOutgoing = entity.FromPeerId != Guid.Empty &&
                         entity.FromPeerId == entity.ToPeerId
            ? false
            : false; // placeholder, real logic lives in ViewModel

        // This method assumes the caller already knows
        // which peer is local vs remote.
        // That keeps protocol concerns out of the mapper.

        return new ChatMessage(
            fromPeer: localProtocolPeerId,
            toPeer: remoteProtocolPeerId,
            content: entity.Content,
            direction: MessageDirection.Outgoing,
            timestamp: entity.Timestamp
        );
    }
}
