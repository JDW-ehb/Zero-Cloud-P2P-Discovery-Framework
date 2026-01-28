using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ZCL.Services.Messaging.Persistence
{
    [Index(nameof(FromPeer), nameof(ToPeer), nameof(Timestamp))]
    public class MessageEntity
    {
        [Key]
        public Guid Id { get; set; }

        public string FromPeer { get; set; } = null!;
        public string ToPeer { get; set; } = null!;
        public string Content { get; set; } = null!;
        public DateTime Timestamp { get; set; }
    }
}
