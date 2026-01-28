using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace ZCL.Services.Messaging.Persistence
{
    public class MessagingDbContext : DbContext
    {
        public DbSet<MessageEntity> Messages => Set<MessageEntity>();

        public MessagingDbContext(DbContextOptions<MessagingDbContext> options)
            : base(options)
        {
        }
    }
}
