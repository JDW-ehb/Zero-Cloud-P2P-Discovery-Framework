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
        Guid messageId,
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content);

    Task<MessageEntity> StoreAsync(
    Guid sessionId,
    Guid fromPeerId,
    Guid toPeerId,
    string content,
    MessageStatus status);

    Task<bool> ExistsAsync(Guid messageId);
    Task UpdateStatusAsync(Guid messageId, MessageStatus status);
    Task<List<MessageEntity>> GetUndeliveredMessagesAsync(Guid toPeerId);
    Task MarkAsDeliveredAsync(Guid messageId);
}
