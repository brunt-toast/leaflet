using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Spectre.Console;
using Tui.Commands;
using Tui.Configuration;
using Tui.Options;
using Tui.Services;

namespace Tui;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using IHost host = BuildHost(args);
            HostedCommandService commandService = host.Services.GetRequiredService<HostedCommandService>();
            await host.RunAsync();
            return commandService.ExitCode;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static IHost BuildHost(string[] args)
    {
        string runtimeConfigPath = Path.Combine(AppContext.BaseDirectory, "config.toml");
        bool requiresConfiguredHost = RequiresConfiguredHost(args);
        TuiLoggingLevelSwitches levelSwitches = new(new LoggingLevelSwitch(), new LoggingLevelSwitch());

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        if (requiresConfiguredHost)
        {
            builder.Configuration.AddEncryptedTomlFile(runtimeConfigPath);
        }

        builder.Logging.ClearProviders();
        builder.Services.Configure<TuiLoggingConfig>(builder.Configuration.GetSection("logging"));
        builder.Services.AddSingleton(levelSwitches);
        builder.Services.AddHostedService<TuiLoggingLevelSwitchUpdater>();
        builder.Services.AddSerilog((services, loggerConfiguration) =>
        {
            TuiLoggingConfig loggingConfig = BuildLoggingConfig(builder.Configuration);
            string filePath = ResolvePath(
                AppContext.BaseDirectory,
                loggingConfig.FilePath);
            EnsureParentDirectoryExists(filePath);
            ApplyLogLevels(levelSwitches, loggingConfig);

            loggerConfiguration
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .MinimumLevel.ControlledBy(levelSwitches.Application)
                .MinimumLevel.Override("Microsoft", levelSwitches.Microsoft)
                .WriteTo.File(
                    path: filePath,
                    rollingInterval: RollingInterval.Day,
                    shared: true);
        });

        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton<IConfigureOptions<TuiAppConfig>, ConfigureTuiAppConfigOptions>();
        builder.Services.AddOptions<TuiAppConfig>();
        builder.Services.AddSingleton(new CommandLineInvocation(args));
        builder.Services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddSingleton(serviceProvider => new AppConfigIoService(
            runtimeConfigPath,
            serviceProvider.GetRequiredService<PasswordService>()));
        builder.Services.AddSingleton<IdentityGeneratorService>();
        builder.Services.AddSingleton<IChatCryptoService, ChatCryptoService>();
        builder.Services.AddHttpClient<MessagingApiClientService>();
        builder.Services.AddHttpClient<ServerDiscoveryService>();
        builder.Services.AddSingleton<TuiApplicationService>();

        builder.Services.AddSingleton<NameOption>();
        builder.Services.AddSingleton<GenerateIdentityCommand>();
        builder.Services.AddSingleton<EditConfigCommand>();
        builder.Services.AddSingleton<TuiRootCommand>();
        builder.Services.AddSingleton<RootCommand>(serviceProvider => serviceProvider.GetRequiredService<TuiRootCommand>());

        builder.Services.AddSingleton<HostedCommandService>();
        builder.Services.AddHostedService(static serviceProvider => serviceProvider.GetRequiredService<HostedCommandService>());

        return builder.Build();
    }

    private static bool RequiresConfiguredHost(string[] args)
    {
        return !args.Any(static arg => arg is "-?" or "-h" or "--help" or "--version");
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

    private static TuiLoggingConfig BuildLoggingConfig(IConfiguration configuration)
    {
        return new TuiLoggingConfig
        {
            FilePath = configuration.GetValue("logging:file_path", "logs/tui-.log") ?? "logs/tui-.log",
            MinimumLevel = configuration.GetValue("logging:minimum_level", "Information") ?? "Information",
            MicrosoftMinimumLevel = configuration.GetValue("logging:microsoft_minimum_level", "Warning") ?? "Warning"
        };
    }

    private static void ApplyLogLevels(TuiLoggingLevelSwitches levelSwitches, TuiLoggingConfig loggingConfig)
    {
        levelSwitches.Application.MinimumLevel = ParseLevel(loggingConfig.MinimumLevel, nameof(TuiLoggingConfig.MinimumLevel));
        levelSwitches.Microsoft.MinimumLevel = ParseLevel(loggingConfig.MicrosoftMinimumLevel, nameof(TuiLoggingConfig.MicrosoftMinimumLevel));
    }

    private static LogEventLevel ParseLevel(string value, string propertyName)
    {
        if (Enum.TryParse(value, ignoreCase: true, out LogEventLevel level))
        {
            return level;
        }

        throw new InvalidOperationException($"logging:{propertyName} must be a valid Serilog log level.");
    }
}
