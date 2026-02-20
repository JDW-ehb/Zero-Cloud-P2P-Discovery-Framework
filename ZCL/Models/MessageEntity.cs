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
    Stored = 1,
    Delivered = 2,
    Received = 3,
    Failed = 4
}

public class MessageEntity
{
    [Key]
    public Guid MessageId { get; set; }

    [Required]
    public Guid FromPeerId { get; set; }

    [Required]
    public Guid ToPeerId { get; set; }

    [Required]
    public Guid SessionId { get; set; }

    [Required]
    public string Content { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public MessageStatus Status { get; set; }

    public string? ClientMessageId { get; set; }

    // Optional future: server-side id
    public long? ServerMessageId { get; set; }

    // Navigation
    public PeerNode? FromPeer { get; set; }
    public PeerNode? ToPeer { get; set; }
}