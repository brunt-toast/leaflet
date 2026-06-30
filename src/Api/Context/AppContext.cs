using Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Context;

public class AppContext : DbContext
{
    public AppContext(DbContextOptions<AppContext> options) : base(options)
    {
        
    }

    internal DbSet<EncryptedMessageEntity> EncryptedMessages => Set<EncryptedMessageEntity>();
}
