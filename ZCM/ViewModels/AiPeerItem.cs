using ZCL.Models;

public sealed class AiPeerItem
{
    public PeerNode Peer { get; set; } = null!;
    public string? Model { get; set; }

    public string Display =>
        $"{Peer.HostName} (Model: {Model ?? "unknown"})";
}
