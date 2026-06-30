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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnownServerEntity>()
            .HasIndex(server => server.Url)
            .IsUnique();
    }
}
