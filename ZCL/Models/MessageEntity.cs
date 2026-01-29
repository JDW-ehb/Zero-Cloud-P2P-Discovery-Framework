using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ZCL.Models;

public enum MessageDirection
{
    Incoming = 0,
    Outgoing = 1
}

public enum MessageStatus
{
    Sent = 0,
    Delivered = 1,
    Failed = 2
}

public class MessageEntity
{
    [Key]
    public Guid MessageId { get; set; }

    [Required]
    public Guid PeerId { get; set; }

    [Required]
    public Guid SessionId { get; set; }

    [Required]
    public string Content { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public MessageDirection Direction { get; set; }

    public MessageStatus Status { get; set; }

    // Navigation (optional but future-proof)
    public PeerNode? Peer { get; set; }
}
