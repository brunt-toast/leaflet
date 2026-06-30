using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Tui.Configuration;
using Tui.Services;

namespace Tui;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        Option<FileInfo?> configOption = new("--config")
        {
            Description = "Path to the TOML configuration file.",
        };

        Option<string?> serverOption = new("--server")
        {
            Description = "Optional server name from config.toml.",
        };

        RootCommand rootCommand = new("Messaging TUI");
        rootCommand.Options.Add(configOption);
        rootCommand.Options.Add(serverOption);
        rootCommand.SetAction(async parseResult =>
        {
            FileInfo configFile = parseResult.GetValue(configOption) ?? new FileInfo("config.toml");
            string? serverName = parseResult.GetValue(serverOption);
            return await RunInteractiveAsync(configFile, serverName);
        });

        Command generateIdentityCommand = new("generate-identity", "Generate a composite identity snippet for config.toml.");
        Option<string?> nameOption = new("--name")
        {
            Description = "Identity name to generate.",
        };
        generateIdentityCommand.Options.Add(nameOption);
        generateIdentityCommand.SetAction(parseResult =>
        {
            string name = parseResult.GetValue(nameOption) ?? "NewIdentity";
            return GenerateIdentity(name);
        });

        rootCommand.Subcommands.Add(generateIdentityCommand);
        return rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunInteractiveAsync(FileInfo configFile, string? serverName)
    {
        using CancellationTokenSource cancellationTokenSource = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        try
        {
            using IHost host = BuildHost(configFile, serverName);
            TuiApplication app = host.Services.GetRequiredService<TuiApplication>();
            await app.RunAsync(cancellationTokenSource.Token);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int GenerateIdentity(string name)
    {
        CompositeIdentityGenerator generator = new();
        CompositeIdentity identity = generator.Generate(name);

        AnsiConsole.MarkupLine($"[green]Generated identity[/] [bold]{Markup.Escape(identity.Name)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Text(
            $"[identities.{identity.Name}]{Environment.NewLine}" +
            $"name = \"{identity.Name}\"{Environment.NewLine}" +
            $"public_key = '''{identity.PublicKeyJson}'''{Environment.NewLine}" +
            $"private_key = '''{identity.PrivateKeyJson}'''{Environment.NewLine}"));
        AnsiConsole.WriteLine();
        return 0;
    }

    private static IHost BuildHost(FileInfo configFile, string? serverName)
    {
        string configPath = ResolveConfigPath(configFile);
        TomlConfigLoader loader = new();
        TomlConfigurationSnapshot snapshot = loader.Load(configPath);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(snapshot.FlattenedValues);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton(snapshot);
        builder.Services.AddSingleton(snapshot.Config);
        builder.Services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        builder.Services.AddSingleton(new TuiLaunchOptions
        {
            ServerName = serverName,
        });
        builder.Services.AddSingleton<CompositeIdentityGenerator>();
        builder.Services.AddSingleton<IChatCryptoService, ChatCryptoService>();
        builder.Services.AddHttpClient<MessagingApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddSingleton<TuiApplication>();
        return builder.Build();
    }

    private static string ResolveConfigPath(FileInfo configFile)
    {
        List<string> candidates =
        [
            configFile.FullName,
            Path.Combine(Environment.CurrentDirectory, configFile.Name),
            Path.Combine(AppContext.BaseDirectory, configFile.Name),
            Path.Combine(Environment.CurrentDirectory, "src", "Tui", configFile.Name),
        ];

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate '{configFile.Name}'. Checked: {string.Join(", ", candidates)}");
    }
}

internal sealed class TuiLaunchOptions
{
    public string? ServerName { get; init; }
}
