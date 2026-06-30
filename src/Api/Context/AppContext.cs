using Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Context;

public class AppContext : DbContext
{
    public AppContext(DbContextOptions<AppContext> options) : base(options)
    {
    }

    internal DbSet<EncryptedMessageEntity> EncryptedMessages => Set<EncryptedMessageEntity>();
    internal DbSet<KnownServerEntity> KnownServers => Set<KnownServerEntity>();
    internal DbSet<MessageShardEntity> MessageShards => Set<MessageShardEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<KnownServerEntity>()
            .HasIndex(server => server.Url)
            .IsUnique();

        modelBuilder.Entity<MessageShardEntity>()
            .HasIndex(shard => new { shard.RoomHash, shard.MessageId, shard.ShardIndex })
            .IsUnique();
    }
}
