using ZCL.Models;

namespace ZCM.ViewModels;

public sealed class AiPeerItem
{
    public PeerNode Peer { get; set; } = null!;
    public string? Model { get; set; }

    public string Display => Peer.HostName;

    public string ModelDisplay =>
        Model ?? "unknown model";

}
public sealed class AiConversationItem
{
    public Guid Id { get; set; }
    public Guid PeerId { get; set; }

    public string PeerName { get; set; } = "";
    public string Model { get; set; } = "";

    public string? Summary { get; set; }

    public string DisplayName => Summary ?? Model;

    public string ModelDisplay => $"model={Model}";
}
