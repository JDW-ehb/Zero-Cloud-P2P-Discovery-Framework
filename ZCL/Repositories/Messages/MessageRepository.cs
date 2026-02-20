using Microsoft.EntityFrameworkCore;
using ZCL.Models;

namespace ZCL.Repositories.Messages;

public sealed class MessageRepository : IMessageRepository
{
    private readonly ServiceDBContext _db;

    public MessageRepository(ServiceDBContext db)
    {
        _db = db;
    }

    // =====================================================
    // STORE OUTGOING (Client Side)
    // =====================================================

    public async Task<MessageEntity> StoreOutgoingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        string? clientMessageId = null)
    {
        return await StoreInternalAsync(
            sessionId,
            fromPeerId,
            toPeerId,
            content,
            MessageStatus.Sent,
            clientMessageId,
            null);
    }

    // =====================================================
    // STORE INCOMING (Client Side)
    // =====================================================

    public async Task<MessageEntity> StoreIncomingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        long? serverMessageId = null)
    {
        return await StoreInternalAsync(
            sessionId,
            fromPeerId,
            toPeerId,
            content,
            MessageStatus.Received,
            null,
            serverMessageId);
    }

    // =====================================================
    // STORE (Coordinator Side)
    // =====================================================

    public async Task<MessageEntity> StoreAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        MessageStatus status,
        string? clientMessageId = null,
        long? serverMessageId = null)
    {
        return await StoreInternalAsync(
            sessionId,
            fromPeerId,
            toPeerId,
            content,
            status,
            clientMessageId,
            serverMessageId);
    }

    private async Task<MessageEntity> StoreInternalAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content,
        MessageStatus status,
        string? clientMessageId,
        long? serverMessageId)
    {
        var entity = new MessageEntity
        {
            MessageId = Guid.NewGuid(),
            SessionId = sessionId,
            FromPeerId = fromPeerId,
            ToPeerId = toPeerId,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Status = status,
            ClientMessageId = clientMessageId,
            ServerMessageId = serverMessageId
        };

        _db.Messages.Add(entity);
        await _db.SaveChangesAsync();

        return entity;
    }

    // =====================================================
    // UPDATE STATUS BY MESSAGE ID
    // =====================================================

    public async Task UpdateStatusAsync(Guid messageId, MessageStatus status)
    {
        var msg = await _db.Messages
            .FirstOrDefaultAsync(m => m.MessageId == messageId);

        if (msg == null)
            return;

        msg.Status = status;
        await _db.SaveChangesAsync();
    }

    // =====================================================
    // UPDATE STATUS BY CLIENT MESSAGE ID (ACK)
    // =====================================================

    public async Task UpdateStatusByClientIdAsync(
        string clientMessageId,
        MessageStatus status,
        long? serverMessageId = null)
    {
        var msg = await _db.Messages
            .FirstOrDefaultAsync(m => m.ClientMessageId == clientMessageId);

        if (msg == null)
            return;

        msg.Status = status;
        msg.ServerMessageId = serverMessageId;

        await _db.SaveChangesAsync();
    }

    // =====================================================
    // FUTURE: GET UNDELIVERED MESSAGES
    // =====================================================

    public async Task<List<MessageEntity>> GetUndeliveredAsync(Guid toPeerId)
    {
        return await _db.Messages
            .Where(m => m.ToPeerId == toPeerId &&
                        m.Status == MessageStatus.Sent)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }
}