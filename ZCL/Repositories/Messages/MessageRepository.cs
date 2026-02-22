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

    public async Task<MessageEntity> StoreOutgoingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content)
    {
        return await StoreAsync(
            sessionId,
            fromPeerId,
            toPeerId,
            content,
            MessageStatus.Sent);
    }

    public async Task<MessageEntity> StoreIncomingAsync(
        Guid sessionId,
        Guid fromPeerId,
        Guid toPeerId,
        string content)
    {
        return await StoreAsync(
            sessionId,
            fromPeerId,
            toPeerId,
            content,
            MessageStatus.Received);
    }

    public Task<MessageEntity> StoreAsync(
    Guid sessionId,
    Guid fromPeerId,
    Guid toPeerId,
    string content,
    MessageStatus status)
    {
        return StoreInternalAsync(sessionId, fromPeerId, toPeerId, content, status);
    }

    private async Task<MessageEntity> StoreInternalAsync(
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
            Status = status
        };

        _db.Messages.Add(entity);
        await _db.SaveChangesAsync();

        return entity;
    }

    public async Task UpdateStatusAsync(Guid messageId, MessageStatus status)
    {
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.MessageId == messageId);
        if (msg == null)
            return;

        msg.Status = status;
        await _db.SaveChangesAsync();
    }

    public async Task<List<MessageEntity>> GetUndeliveredMessagesAsync(Guid toPeerId)
    {
        return await _db.Messages
            .Where(m => m.ToPeerId == toPeerId && !m.Delivered)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }

    public async Task MarkAsDeliveredAsync(Guid messageId)
    {
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.MessageId == messageId);
        if (msg == null)
            return;

        msg.Delivered = true;
        await _db.SaveChangesAsync();
    }




}
