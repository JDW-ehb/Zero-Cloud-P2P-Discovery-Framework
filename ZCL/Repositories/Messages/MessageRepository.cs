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

    // ---------------------------------------------------------
    // Exists (Deduplication Check)
    // ---------------------------------------------------------

    public async Task<bool> ExistsAsync(Guid messageId)
    {
        return await _db.Messages
            .AnyAsync(m => m.MessageId == messageId);
    }

    // ---------------------------------------------------------
    // Outgoing
    // ---------------------------------------------------------

    public async Task<MessageEntity> StoreOutgoingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content)
    {
        var entity = new MessageEntity
        {
            MessageId = Guid.NewGuid(),
            SessionId = sessionId,
            FromPeerId = fromPeerId,
            ToPeerId = toPeerId,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Sent,
            Delivered = false
        };

        _db.Messages.Add(entity);
        await _db.SaveChangesAsync();

        return entity;
    }

    // ---------------------------------------------------------
    // Incoming (WITH MESSAGE ID FROM NETWORK)
    // ---------------------------------------------------------

    public async Task<MessageEntity> StoreIncomingAsync(
        Guid messageId,
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content)
    {
        var entity = new MessageEntity
        {
            MessageId = messageId, // IMPORTANT
            SessionId = sessionId,
            FromPeerId = fromPeerId,
            ToPeerId = toPeerId,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Status = MessageStatus.Received,
            Delivered = true
        };

        _db.Messages.Add(entity);
        await _db.SaveChangesAsync();

        return entity;
    }

    // ---------------------------------------------------------
    // Undelivered
    // ---------------------------------------------------------

    public async Task<List<MessageEntity>> GetUndeliveredMessagesAsync(Guid toPeerId)
    {
        return await _db.Messages
            .Where(m => m.ToPeerId == toPeerId && !m.Delivered)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }

    public async Task MarkAsDeliveredAsync(Guid messageId)
    {
        var msg = await _db.Messages
            .FirstOrDefaultAsync(m => m.MessageId == messageId);

        if (msg == null)
            return;

        msg.Delivered = true;
        await _db.SaveChangesAsync();
    }

    // ---------------------------------------------------------
    // Status Update
    // ---------------------------------------------------------

    public async Task UpdateStatusAsync(Guid messageId, MessageStatus status)
    {
        var msg = await _db.Messages
            .FirstOrDefaultAsync(m => m.MessageId == messageId);

        if (msg == null)
            return;

        msg.Status = status;
        await _db.SaveChangesAsync();
    }

    public async Task<MessageEntity> StoreAsync(
    Guid sessionId,
    Guid fromPeerId,
    Guid toPeerId,
    string content,
    MessageStatus status)
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
            Delivered = status == MessageStatus.Received
        };

        _db.Messages.Add(entity);
        await _db.SaveChangesAsync();

        return entity;
    }
}