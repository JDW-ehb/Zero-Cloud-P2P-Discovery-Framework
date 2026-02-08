using ZCL.Models;

namespace ZCM.ViewModels;

public sealed class ConversationItem : BindableObject
{
    public PeerNode Peer { get; }
    public string DisplayName => Peer.HostName ?? Peer.ProtocolPeerId ?? Peer.PeerId.ToString();

    public bool IsOnline => Peer.OnlineStatus == PeerOnlineStatus.Online;

    private string _lastMessage = "No messages yet";
    public string LastMessage
    {
        get => _lastMessage;
        set { _lastMessage = value; OnPropertyChanged(); }
    }

    private DateTime? _lastTimestamp;
    public DateTime? LastTimestamp
    {
        get => _lastTimestamp;
        set { _lastTimestamp = value; OnPropertyChanged(); }
    }

    public ConversationItem(PeerNode peer)
    {
        Peer = peer;
    }
}
