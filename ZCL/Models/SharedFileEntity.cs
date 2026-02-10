using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ZCL.Models;

public sealed class SharedFileEntity
{
    [Key]
    public Guid FileId { get; set; }

    [Required]
    public Guid PeerRefId { get; set; }

    [ForeignKey(nameof(PeerRefId))]
    public PeerNode Peer { get; set; } = null!;

    [Required]
    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    [Required]
    public string FileType { get; set; } = string.Empty;

    [Required]
    public string Checksum { get; set; } = string.Empty;

    [Required]
    public string LocalPath { get; set; } = string.Empty;

    public DateTime SharedSince { get; set; }

    public bool IsAvailable { get; set; }
}
