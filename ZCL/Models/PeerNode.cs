using System;
using System.ComponentModel.DataAnnotations;

namespace ZCL.Models;

public enum PeerOnlineStatus
{
    Offline = 0,
    Online = 1,
    Unknown = 2
}

public class PeerNode
{
    [Key]
    public Guid PeerId { get; set; }

    [Required]
    public string ProtocolPeerId { get; set; } = string.Empty;

    [Required]
    public string IpAddress { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    public PeerOnlineStatus OnlineStatus { get; set; }
}
