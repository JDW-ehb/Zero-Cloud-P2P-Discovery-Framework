using ZCL.Models;

namespace ZCL.Repositories.Peers;

public interface IPeerRepository
{
    Task<string> GetOrCreateLocalProtocolPeerIdAsync(
        string hostName,
        string ipAddress = "127.0.0.1",
        CancellationToken ct = default);

    Task<PeerNode> GetOrCreateAsync(
        string protocolPeerId,
        string? ipAddress = null,
        string? hostName = null,
        CancellationToken ct = default);
    Task<PeerNode?> GetByProtocolPeerIdAsync(
    string protocolPeerId,
    CancellationToken ct = default);


    Task<PeerNode?> GetByProtocolIdAsync(string protocolPeerId, CancellationToken ct = default);
    Task<PeerNode> GetLocalPeerAsync(CancellationToken ct = default);
    Task<List<PeerNode>> GetAllAsync(CancellationToken ct = default);
    Task<Guid?> GetLocalPeerIdAsync(CancellationToken ct = default);
    Task<PeerNode?> GetCoordinatorAsync(CancellationToken ct = default);
}
