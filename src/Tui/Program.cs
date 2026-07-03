using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        using IHost host = BuildHost(args);
        HostedCommandService commandService = host.Services.GetRequiredService<HostedCommandService>();
        await host.RunAsync();
        return commandService.ExitCode;
    }

    private static IHost BuildHost(string[] args)
    {
        string runtimeConfigPath = Path.Combine(AppContext.BaseDirectory, "config.toml");
        bool requiresConfiguredHost = RequiresConfiguredHost(args);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        if (requiresConfiguredHost)
        {
            builder.Configuration.AddEncryptedTomlFile(runtimeConfigPath);
        }

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton<IConfigureOptions<TuiAppConfig>, ConfigureTuiAppConfigOptions>();
        builder.Services.AddOptions<TuiAppConfig>();
        builder.Services.AddSingleton(new CommandLineInvocation(args));
        builder.Services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddSingleton(serviceProvider => new AppConfigIoService(
            runtimeConfigPath,
            serviceProvider.GetRequiredService<PasswordService>()));
        builder.Services.AddSingleton<CompositeIdentityGeneratorService>();
        builder.Services.AddSingleton<IChatCryptoService, ChatCryptoService>();
        builder.Services.AddHttpClient<MessagingApiClientService>();
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
}
