using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Sqlite;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Reflection.Metadata;
using System.Text;

namespace ZCL.Models
{
    public class ServiceDBContext : DbContext
    {
        public DbSet<Service> Services => Set<Service>();

        public DbSet<MessageEntity> Messages => Set<MessageEntity>();
        public DbSet<PeerNode> Peers => Set<PeerNode>();

        public ServiceDBContext(DbContextOptions<ServiceDBContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MessageEntity>()
                .HasOne(m => m.FromPeer)
                .WithMany()
                .HasForeignKey(m => m.FromPeerId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MessageEntity>()
                .HasOne(m => m.ToPeer)
                .WithMany()
                .HasForeignKey(m => m.ToPeerId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MessageEntity>()
                .HasIndex(m => new
                {
                    m.FromPeerId,
                    m.ToPeerId,
                    m.Timestamp
                });

            base.OnModelCreating(modelBuilder);
        }

    }

    [Index(nameof(name), nameof(address), nameof(port), nameof(peerGuid), IsUnique = true)]
    public class Service
    {
        [Key]
        public int id { get; set; }

        public string name { get; set; }
        public string address { get; set; }
        public UInt16 port { get; set; }
        public Guid peerGuid { get; set; }
    }
}

