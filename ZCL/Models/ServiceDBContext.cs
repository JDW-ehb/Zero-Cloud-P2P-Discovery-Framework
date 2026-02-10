using Microsoft.EntityFrameworkCore;

namespace ZCL.Models;

public class ServiceDBContext : DbContext
{
    public DbSet<Service> Services => Set<Service>();
    public DbSet<PeerNode> PeerNodes => Set<PeerNode>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<SharedFileEntity> SharedFiles => Set<SharedFileEntity>();
    public DbSet<FileTransferEntity> FileTransfers => Set<FileTransferEntity>();


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
            .HasIndex(m => new { m.FromPeerId, m.ToPeerId, m.Timestamp });

        modelBuilder.Entity<PeerNode>()
            .HasIndex(p => p.IsLocal)
            .IsUnique()
            .HasFilter("\"IsLocal\" = 1");

        base.OnModelCreating(modelBuilder);
    }
}
