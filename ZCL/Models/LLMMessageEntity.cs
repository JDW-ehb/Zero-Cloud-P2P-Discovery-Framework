using System.ComponentModel.DataAnnotations;

public class LLMMessageEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    [Required]
    public string Content { get; set; } = "";

    public bool IsUser { get; set; }

    public DateTime Timestamp { get; set; }
}
