using ZCL.Models;

namespace ZCL.Services.Messaging;

public interface IChatQueryService
{
    Task<List<PeerNode>> GetPeersAsync();
    Task<Guid?> GetLocalPeerIdAsync();
    Task<List<MessageEntity>> GetHistoryAsync(Guid localPeerId, Guid remotePeerId);

    Task<MessageEntity?> GetLastMessageBetweenAsync(Guid localPeerId, Guid remotePeerDbId, CancellationToken ct = default);
}
