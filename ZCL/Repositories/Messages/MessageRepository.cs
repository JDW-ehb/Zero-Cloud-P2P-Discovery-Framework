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
            MessageStatus.Delivered);
    }

    private async Task<MessageEntity> StoreAsync(
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

    public async Task<List<MessageEntity>> GetConversationAsync(Guid localPeerId, Guid remotePeerId)
    {
        return await _db.Messages
            .Where(m =>
                (m.FromPeerId == localPeerId && m.ToPeerId == remotePeerId) ||
                (m.FromPeerId == remotePeerId && m.ToPeerId == localPeerId))
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }

}
