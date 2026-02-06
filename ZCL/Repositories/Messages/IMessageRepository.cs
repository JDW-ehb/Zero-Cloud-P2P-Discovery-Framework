using ZCL.Models;

namespace ZCL.Repositories.Messages;

public interface IMessageRepository
{
    Task<MessageEntity> StoreOutgoingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content);

    Task<MessageEntity> StoreIncomingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content);
}
