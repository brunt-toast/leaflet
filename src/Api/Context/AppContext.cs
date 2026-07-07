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
            .Property(message => message.RoomHash)
            .HasConversion(StorageValueConverters.RoomHashConverter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<EncryptedMessageEntity>()
            .Property(message => message.SenderPublicKey)
            .HasConversion(StorageValueConverters.Utf8StringConverter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<EncryptedMessageEntity>()
            .Property(message => message.Nonce)
            .HasConversion(StorageValueConverters.Base64Converter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<EncryptedMessageEntity>()
            .Property(message => message.CypherText)
            .HasConversion(StorageValueConverters.Base64Converter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<EncryptedMessageEntity>()
            .Property(message => message.Signature)
            .HasConversion(StorageValueConverters.SignatureConverter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<EncryptedMessageEntity>()
            .Property(message => message.Id)
            .ValueGeneratedNever();

        modelBuilder.Entity<EncryptedMessageEntity>()
            .HasKey(message => new { message.RoomHash, message.Id });

        modelBuilder.Entity<InstanceStateEntity>()
            .HasKey(state => state.Id);

        modelBuilder.Entity<KnownServerEntity>()
            .Property(server => server.FirstSeenAtUtc)
            .HasConversion(StorageValueConverters.UtcDateTimeTicksConverter)
            .HasColumnType("INTEGER");

        modelBuilder.Entity<KnownServerEntity>()
            .Property(server => server.LastSeenAtUtc)
            .HasConversion(StorageValueConverters.NullableUtcDateTimeTicksConverter)
            .HasColumnType("INTEGER");

        modelBuilder.Entity<KnownServerEntity>()
            .HasIndex(server => server.Url)
            .IsUnique();

        modelBuilder.Entity<MessageShardEntity>()
            .Property(shard => shard.RoomHash)
            .HasConversion(StorageValueConverters.RoomHashConverter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<MessageShardEntity>()
            .Property(shard => shard.StoredAtUtc)
            .HasConversion(StorageValueConverters.UtcDateTimeTicksConverter)
            .HasColumnType("INTEGER");

        modelBuilder.Entity<MessageShardEntity>()
            .HasIndex(shard => new { shard.RoomHash, shard.MessageId, shard.ShardIndex })
            .IsUnique();

        modelBuilder.Entity<RoomClockEntity>()
            .Property(clock => clock.RoomHash)
            .HasConversion(StorageValueConverters.RoomHashConverter)
            .HasColumnType("BLOB");

        modelBuilder.Entity<RoomClockEntity>()
            .HasKey(clock => clock.RoomHash);
    }
}
