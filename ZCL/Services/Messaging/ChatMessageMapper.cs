using ZCL.Models;

namespace ZCL.Services.Messaging;

public static class ChatMessageMapper
{
    public static ChatMessage Outgoing(
        string localProtocolPeerId,
        string remoteProtocolPeerId,
        MessageEntity entity)
        => new(
            fromPeer: localProtocolPeerId,
            toPeer: remoteProtocolPeerId,
            content: entity.Content,
            direction: MessageDirection.Outgoing,
            timestamp: entity.Timestamp
        );

    public static ChatMessage Incoming(
        string fromProtocolPeerId,
        string toProtocolPeerId,
        MessageEntity entity)
        => new(
            fromPeer: fromProtocolPeerId,
            toPeer: toProtocolPeerId,
            content: entity.Content,
            direction: MessageDirection.Incoming,
            timestamp: entity.Timestamp
        );

    public static ChatMessage FromHistory(
        MessageEntity entity,
        Guid localPeerGuid,
        string localProtocolPeerId,
        string remoteProtocolPeerId)
    {
        var isOutgoing = entity.FromPeerId == localPeerGuid;

        return isOutgoing
            ? Outgoing(localProtocolPeerId, remoteProtocolPeerId, entity)
            : Incoming(remoteProtocolPeerId, localProtocolPeerId, entity);
    }

    public static IEnumerable<ChatMessage> FromHistoryList(
        IEnumerable<MessageEntity> history,
        Guid localPeerGuid,
        string localProtocolPeerId,
        string remoteProtocolPeerId)
    {
        foreach (var msg in history)
            yield return FromHistory(msg, localPeerGuid, localProtocolPeerId, remoteProtocolPeerId);
    }
}
