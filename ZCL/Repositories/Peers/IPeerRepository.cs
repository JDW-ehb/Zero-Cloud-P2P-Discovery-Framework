using ZCL.Models;

namespace ZCL.Repositories.Peers;

public interface IPeerRepository
{
    Task<PeerNode> GetOrCreateAsync(
        string protocolPeerId,
        string? ipAddress,
        string? hostName,
        bool isLocal = false);
    Task<PeerNode> GetOrCreateAsync(string protocolPeerId);

    Task<Guid?> GetLocalPeerIdAsync(); Task<PeerNode?>

    GetByProtocolIdAsync(string protocolPeerId);

    Task<PeerNode> GetLocalPeerAsync();

    Task<List<PeerNode>> GetAllAsync();

    Task EnsureLocalPeerAsync(string localProtocolPeerId);
}
