using ZCL.Models;

namespace ZCL.Repositories.Peers;

public interface IPeerRepository
{
    Task<PeerNode> GetOrCreateAsync(
        string protocolPeerId,
        string? ipAddress = null,
        string? hostName = null,
        bool isLocal = false,
        CancellationToken ct = default);

    Task<PeerNode?> GetByProtocolIdAsync(string protocolPeerId, CancellationToken ct = default);
    Task<PeerNode> GetLocalPeerAsync(CancellationToken ct = default);
    Task<List<PeerNode>> GetAllAsync(CancellationToken ct = default);
    Task EnsureLocalPeerAsync(string localProtocolPeerId, CancellationToken ct = default);
    Task<Guid?> GetLocalPeerIdAsync(CancellationToken ct = default);
}
