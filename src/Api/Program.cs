using Api.Configuration;
using Api.Services;
using AppDbContext = Api.Context.AppContext;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Threading.RateLimiting;

namespace Api;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        ApiLoggingLevelSwitches levelSwitches = new(new LoggingLevelSwitch(), new LoggingLevelSwitch());

        builder.Services.AddSingleton(levelSwitches);
        builder.Services.AddHostedService<ApiLoggingLevelSwitchUpdater>();

        builder.Logging.ClearProviders();
        builder.Host.UseSerilog((context, services, loggerConfiguration) =>
        {
            ApiLoggingOptions loggingOptions = context.Configuration
                .GetSection(ApiLoggingOptions.SectionName)
                .Get<ApiLoggingOptions>()
                ?? new ApiLoggingOptions();
            string sqliteDbPath = ResolvePath(context.HostingEnvironment.ContentRootPath, loggingOptions.SqliteDbPath);
            EnsureParentDirectoryExists(sqliteDbPath);
            ApplyLogLevels(levelSwitches, loggingOptions);

            loggerConfiguration
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .MinimumLevel.ControlledBy(levelSwitches.Application)
                .MinimumLevel.Override("Microsoft", levelSwitches.Microsoft)
                .WriteTo.SQLite(
                    sqliteDbPath: sqliteDbPath,
                    tableName: "Logs",
                    storeTimestampInUtc: true);
        });

        string connectionString = builder.Configuration.GetConnectionString(ConnectionStringKeys.AppContext)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringKeys.AppContext}' was not found.");

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connectionString));
        builder.Services.Configure<ApiLoggingOptions>(builder.Configuration.GetSection(ApiLoggingOptions.SectionName));
        builder.Services.Configure<PeerSyncOptions>(builder.Configuration.GetSection(PeerSyncOptions.SectionName));
        builder.Services.Configure<ErasureCodingOptions>(builder.Configuration.GetSection(ErasureCodingOptions.SectionName));
        builder.Services.Configure<MessageRequestOptions>(builder.Configuration.GetSection(MessageRequestOptions.SectionName));

        RateLimitingOptions rateLimitingOptions = builder.Configuration
            .GetSection(RateLimitingOptions.SectionName)
            .Get<RateLimitingOptions>()
            ?? new RateLimitingOptions();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: clientKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitingOptions.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimitingOptions.WindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
        });


        builder.Services.AddHttpClient(nameof(PeerSyncService), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddHttpClient(nameof(MessageErasureCodingService), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        builder.Services.AddSingleton<IMessageShardDistributionQueue, MessageShardDistributionQueue>();
        builder.Services.AddScoped<IMessageIdentityService, MessageIdentityService>();
        builder.Services.AddScoped<IPeerSyncService, PeerSyncService>();
        builder.Services.AddScoped<IMessageErasureCodingService, MessageErasureCodingService>();
        builder.Services.AddHostedService<PeerPollingBackgroundService>();
        builder.Services.AddHostedService<MessageIntegrityBackgroundService>();
        builder.Services.AddHostedService<MessageShardDistributionBackgroundService>();

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        try
        {
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

            if (app.Configuration.GetValue("UseHttpsRedirection", true))
            {
                app.UseHttpsRedirection();
            }

            app.UseSerilogRequestLogging();
            app.UseRateLimiter();
            app.UseAuthorization();
            app.MapControllers();
            app.Run();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static string ResolvePath(string rootPath, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.GetFullPath(Path.Combine(rootPath, configuredPath));
    }

    private static void EnsureParentDirectoryExists(string filePath)
    {
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    private static void ApplyLogLevels(ApiLoggingLevelSwitches levelSwitches, ApiLoggingOptions options)
    {
        levelSwitches.Application.MinimumLevel = ParseLevel(options.MinimumLevel, nameof(ApiLoggingOptions.MinimumLevel));
        levelSwitches.Microsoft.MinimumLevel = ParseLevel(options.MicrosoftMinimumLevel, nameof(ApiLoggingOptions.MicrosoftMinimumLevel));
    }

    private static LogEventLevel ParseLevel(string value, string propertyName)
    {
        if (Enum.TryParse(value, ignoreCase: true, out LogEventLevel level))
        {
            return level;
        }

        throw new InvalidOperationException($"{ApiLoggingOptions.SectionName}:{propertyName} must be a valid Serilog log level.");
    }
}
