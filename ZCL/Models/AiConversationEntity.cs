using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace ZCL.Models
{
    public class AiConversationEntity
    {
        [Key]
        public Guid Id { get; set; }

        public Guid PeerId { get; set; }

        public string Model { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        public string? Summary { get; set; }  

        public PeerNode? Peer { get; set; }
    }


}
