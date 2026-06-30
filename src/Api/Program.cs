using Api.Configuration;
using Api.Services;
using AppDbContext = Api.Context.AppContext;
using Microsoft.EntityFrameworkCore;

namespace Api;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        string connectionString = builder.Configuration.GetConnectionString(ConnectionStringKeys.AppContext)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringKeys.AppContext}' was not found.");

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connectionString));
        builder.Services.Configure<PeerSyncOptions>(builder.Configuration.GetSection(PeerSyncOptions.SectionName));
        builder.Services.Configure<ErasureCodingOptions>(builder.Configuration.GetSection(ErasureCodingOptions.SectionName));
        builder.Services.AddHttpClient(nameof(PeerSyncService), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddHttpClient(nameof(MessageErasureCodingService), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddScoped<IMessageIdentityService, MessageIdentityService>();
        builder.Services.AddScoped<IPeerSyncService, PeerSyncService>();
        builder.Services.AddScoped<IMessageErasureCodingService, MessageErasureCodingService>();
        builder.Services.AddHostedService<PeerPollingBackgroundService>();

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        WebApplication app = builder.Build();
        using (IServiceScope scope = app.Services.CreateScope())
        {
            AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Database.EnsureCreated();
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.Run();
    }
}
