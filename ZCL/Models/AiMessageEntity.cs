using System.ComponentModel.DataAnnotations;
using ZCL.Models;

public class AiMessageEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid PeerId { get; set; }   

    [Required]
    public string Model { get; set; } = "";

    [Required]
    public string Content { get; set; } = "";

    public bool IsUser { get; set; }

    public DateTime Timestamp { get; set; }

    public PeerNode? Peer { get; set; }
}
