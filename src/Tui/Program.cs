using System.CommandLine;
using System.Text;
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

        Command encryptConfigCommand = new("encrypt-config", "Encrypt config.toml using a password.");
        encryptConfigCommand.Options.Add(configOption);
        encryptConfigCommand.SetAction(parseResult =>
        {
            FileInfo configFile = parseResult.GetValue(configOption) ?? new FileInfo("config.toml");
            return EncryptConfig(configFile);
        });

        Command decryptConfigCommand = new("decrypt-config", "Decrypt config.toml using a password.");
        decryptConfigCommand.Options.Add(configOption);
        decryptConfigCommand.SetAction(parseResult =>
        {
            FileInfo configFile = parseResult.GetValue(configOption) ?? new FileInfo("config.toml");
            return DecryptConfig(configFile);
        });

        rootCommand.Subcommands.Add(generateIdentityCommand);
        rootCommand.Subcommands.Add(encryptConfigCommand);
        rootCommand.Subcommands.Add(decryptConfigCommand);
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
        ConfigProtectionService protectionService = new();
        string configContent = File.ReadAllText(configPath);
        if (protectionService.IsEncrypted(configContent))
        {
            string password = PromptForPassword(
                "Config password",
                "Enter the password to decrypt config.toml in memory.");
            configContent = protectionService.Decrypt(configContent, password);
        }

        TomlConfigurationSnapshot snapshot = loader.LoadFromContent(configPath, configContent);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(snapshot.FlattenedValues);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton(snapshot);
        builder.Services.AddSingleton(snapshot.Config);
        builder.Services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        builder.Services.AddSingleton<IConfigProtectionService, ConfigProtectionService>();
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

    private static int EncryptConfig(FileInfo configFile)
    {
        string configPath = ResolveConfigPath(configFile);
        ConfigProtectionService protectionService = new();
        string content = File.ReadAllText(configPath);

        if (protectionService.IsEncrypted(content))
        {
            AnsiConsole.MarkupLine("[yellow]Config is already encrypted.[/]");
            return 0;
        }

        string password = PromptForPassword("New password", "Enter a password to encrypt config.toml.");
        string confirmPassword = PromptForPassword("Confirm password", "Re-enter the password.");
        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[red]Passwords did not match.[/]");
            return 1;
        }

        string encryptedContent = protectionService.Encrypt(content, password);
        File.WriteAllText(configPath, encryptedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AnsiConsole.MarkupLine($"[green]Encrypted[/] {Markup.Escape(configPath)}");
        return 0;
    }

    private static int DecryptConfig(FileInfo configFile)
    {
        string configPath = ResolveConfigPath(configFile);
        ConfigProtectionService protectionService = new();
        string content = File.ReadAllText(configPath);

        if (!protectionService.IsEncrypted(content))
        {
            AnsiConsole.MarkupLine("[yellow]Config is already plaintext.[/]");
            return 0;
        }

        string password = PromptForPassword("Config password", "Enter the password to decrypt config.toml.");
        string decryptedContent = protectionService.Decrypt(content, password);
        File.WriteAllText(configPath, decryptedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AnsiConsole.MarkupLine($"[green]Decrypted[/] {Markup.Escape(configPath)}");
        return 0;
    }

    private static string PromptForPassword(string label, string prompt)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>(prompt)
                .PromptStyle("green")
                .Secret()
                .Validate(input => string.IsNullOrWhiteSpace(input)
                    ? ValidationResult.Error($"[red]{Markup.Escape(label)} cannot be empty.[/]")
                    : ValidationResult.Success()));
    }
}

internal sealed class TuiLaunchOptions
{
    public string? ServerName { get; init; }
}
