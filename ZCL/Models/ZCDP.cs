using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Sqlite;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Net;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text;

namespace ZCL.Models
{
    public class ServiceDBContext : DbContext
    {
        public DbSet<Service> Services => Set<Service>();
        public DbSet<Peer> Peers => Set<Peer>();

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
            modelBuilder.Entity<PeerNode>()
            .HasIndex(p => p.IsLocal)
            .IsUnique()
            .HasFilter("\"IsLocal\" = 1");


            base.OnModelCreating(modelBuilder);
        }

    }

    [Index(nameof(Name), nameof(Address), nameof(Guid), IsUnique = true)]
    public class Peer : INotifyPropertyChanged
    {
        [Key]
        public int PeerId { get; set; }

        public string Name { get; set; }
        public string Address { get; set; }
        public Guid Guid { get; set; }

        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        
        public ICollection<Service> Services { get; set; }


        [NotMapped]
        private string _lastSeenSeconds;
        [NotMapped]
        public string LastSeenSeconds
        {
            get => _lastSeenSeconds;
            set { _lastSeenSeconds = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

    }

    [Index(nameof(Name), nameof(Address), nameof(Port), IsUnique = true)]
    public class Service
    {
        [Key]
        public int ServiceId { get; set; }

        public string Name { get; set; }
        public string Address { get; set; }
        public UInt16 Port { get; set; }

        public int PeerRefId { get; set; }
        [ForeignKey("PeerRefId")]
        public Peer Peer { get; set; }
    }
}

