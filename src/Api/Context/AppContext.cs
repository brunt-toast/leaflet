using Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Context;

internal class AppContext : DbContext
{
    public AppContext(DbContextOptions<AppContext> options) : base(options)
    {
        
    }

    public DbSet<EncryptedMessageEntity> EncryptedMessages => Set<EncryptedMessageEntity>();
}
