using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ZCL.Models;

[Index(nameof(Name), nameof(Address), nameof(Port), nameof(PeerRefId), IsUnique = true)]
public class Service
{
    [Key]
    public int ServiceId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Address { get; set; } = string.Empty;

    public ushort Port { get; set; }

    public Guid PeerRefId { get; set; }

    public string? Metadata { get; set; }  

    [ForeignKey(nameof(PeerRefId))]
    public PeerNode? Peer { get; set; }
}
