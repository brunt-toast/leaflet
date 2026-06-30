using Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Context;

public class AppContext : DbContext
{
    public AppContext(DbContextOptions<AppContext> options) : base(options)
    {
    }

    internal DbSet<EncryptedMessageEntity> EncryptedMessages => Set<EncryptedMessageEntity>();
    internal DbSet<InstanceStateEntity> InstanceStates => Set<InstanceStateEntity>();
    internal DbSet<KnownServerEntity> KnownServers => Set<KnownServerEntity>();
    internal DbSet<MessageShardEntity> MessageShards => Set<MessageShardEntity>();
    internal DbSet<RoomClockEntity> RoomClocks => Set<RoomClockEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EncryptedMessageEntity>()
            .Property(message => message.Id)
            .ValueGeneratedNever();

        modelBuilder.Entity<EncryptedMessageEntity>()
            .HasIndex(message => new { message.RoomHash, message.Id });

        modelBuilder.Entity<InstanceStateEntity>()
            .HasKey(state => state.Id);

        modelBuilder.Entity<KnownServerEntity>()
            .HasIndex(server => server.Url)
            .IsUnique();

        modelBuilder.Entity<MessageShardEntity>()
            .HasIndex(shard => new { shard.RoomHash, shard.MessageId, shard.ShardIndex })
            .IsUnique();

        modelBuilder.Entity<RoomClockEntity>()
            .HasKey(clock => clock.RoomHash);
    }
}
