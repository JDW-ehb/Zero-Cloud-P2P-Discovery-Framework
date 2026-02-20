using ZCL.Models;

namespace ZCL.Repositories.Messages;

public interface IMessageRepository
{
    // Basic store (used by coordinator)
    Task<MessageEntity> StoreAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        MessageStatus status,
        string? clientMessageId = null,
        long? serverMessageId = null);

    // Client outgoing
    Task<MessageEntity> StoreOutgoingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        string? clientMessageId = null);

    // Client incoming
    Task<MessageEntity> StoreIncomingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        long? serverMessageId = null);

    Task UpdateStatusAsync(Guid messageId, MessageStatus status);

    Task UpdateStatusByClientIdAsync(
        string clientMessageId,
        MessageStatus status,
        long? serverMessageId = null);

    Task<List<MessageEntity>> GetUndeliveredAsync(Guid toPeerId);
}