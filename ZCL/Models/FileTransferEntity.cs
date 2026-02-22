using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ZCL.Models;

public enum FileTransferStatus
{
    Requested = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3
}

public sealed class FileTransferEntity
{
    [Key]
    public Guid TransferId { get; set; }

    // FK -> SharedFileEntity.Id (local PK)
    [Required]
    public Guid SharedFileId { get; set; }

    [ForeignKey(nameof(SharedFileId))]
    public SharedFileEntity File { get; set; } = null!;

    [Required]
    public Guid PeerRefId { get; set; }

    [ForeignKey(nameof(PeerRefId))]
    public PeerNode Peer { get; set; } = null!;

    [Required]
    public Guid SessionId { get; set; }

    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Checksum { get; set; } = string.Empty;

    public FileTransferStatus Status { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}