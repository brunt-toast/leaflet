using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Tui.Configuration;

internal sealed class ConfigureTuiAppConfigOptions(IConfiguration configuration) : IConfigureOptions<TuiAppConfig>
{
    public void Configure(TuiAppConfig options)
    {
        options.Core = new TuiCoreConfig
        {
            HistoryCount = configuration.GetValue("core:history_count", 50),
            RefreshIntervalSeconds = Math.Max(1, configuration.GetValue("core:refresh_interval_seconds", 10))
        };
        options.Logging = new TuiLoggingConfig
        {
            FilePath = configuration.GetValue("logging:file_path", "logs/tui-.log") ?? "logs/tui-.log",
            MinimumLevel = configuration.GetValue("logging:minimum_level", "Information") ?? "Information",
            MicrosoftMinimumLevel = configuration.GetValue("logging:microsoft_minimum_level", "Warning") ?? "Warning"
        };

        options.Identities = BuildIdentities();
        options.Servers = BuildServers();
        options.RoomsRoot = BuildRooms();
    }

    private IReadOnlyDictionary<string, IdentityConfig> BuildIdentities()
    {
        IConfigurationSection identitiesSection = configuration.GetSection("identities");
        if (!identitiesSection.Exists())
        {
            throw new InvalidOperationException("config.toml must contain an [identities] section.");
        }

        Dictionary<string, IdentityConfig> identities = [];
        foreach (IConfigurationSection identitySection in identitiesSection.GetChildren())
        {
            identities[identitySection.Key] = new IdentityConfig
            {
                Name = GetRequiredValue(identitySection, "name", $"identities.{identitySection.Key}"),
                PublicKey = GetRequiredValue(identitySection, "public_key", $"identities.{identitySection.Key}"),
                PrivateKey = GetRequiredValue(identitySection, "private_key", $"identities.{identitySection.Key}")
            };
        }

        if (identities.Count == 0)
        {
            throw new InvalidOperationException("config.toml must define at least one identity.");
        }

        return identities;
    }

    private ServerClusterConfig BuildServers()
    {
        IConfigurationSection serversSection = configuration.GetSection("servers");
        if (!serversSection.Exists())
        {
            throw new InvalidOperationException("config.toml must contain a [servers] section.");
        }

        IConfigurationSection mainServerSection = serversSection.GetSection("main");
        if (!mainServerSection.Exists())
        {
            throw new InvalidOperationException("config.toml must contain a [servers.main] section.");
        }

        List<ServerConfig> backups = [];
        string[] backupUrls = mainServerSection.GetSection("backup_urls").Get<string[]>() ?? [];
        for (int index = 0; index < backupUrls.Length; index++)
        {
            string backupUrl = backupUrls[index];
            if (string.IsNullOrWhiteSpace(backupUrl))
            {
                throw new InvalidOperationException($"Missing required string 'backup_urls[{index}]' at [servers.main].");
            }

            backups.Add(new ServerConfig
            {
                Name = $"backup-{index + 1}",
                Url = backupUrl
            });
        }

        return new ServerClusterConfig
        {
            Main = new ServerConfig
            {
                Name = "main",
                Url = GetRequiredValue(mainServerSection, "url", "servers.main")
            },
            Backups = backups
        };
    }

    private RoomGroupNode BuildRooms()
    {
        IConfigurationSection roomsSection = configuration.GetSection("rooms");
        if (!roomsSection.Exists())
        {
            throw new InvalidOperationException("config.toml must contain a [rooms] section.");
        }

        List<RoomTreeNode> children = [];
        children.AddRange(roomsSection.GetChildren().Select(childSection => BuildRoomNode(childSection, "rooms")));

        return new RoomGroupNode
        {
            Name = "rooms",
            Path = "rooms",
            Children = children.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static RoomTreeNode BuildRoomNode(IConfigurationSection section, string parentPath)
    {
        string fullPath = $"{parentPath}.{section.Key}";
        string? key = section["key"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            return new RoomLeafNode
            {
                Name = section.Key,
                Path = fullPath,
                Key = key,
                IdentityName = GetRequiredValue(section, "identity", fullPath)
            };
        }

        List<RoomTreeNode> children = [];
        children.AddRange(section.GetChildren().Select(childSection => BuildRoomNode(childSection, fullPath)));

        return new RoomGroupNode
        {
            Name = section.Key,
            Path = fullPath,
            Children = children.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static string GetRequiredValue(IConfiguration configuration, string key, string path)
    {
        string? value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Missing required string '{key}' at [{path}].");
    }
}
