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

    private IReadOnlyDictionary<string, ServerConfig> BuildServers()
    {
        IConfigurationSection serversSection = configuration.GetSection("servers");
        if (!serversSection.Exists())
        {
            throw new InvalidOperationException("config.toml must contain a [servers] section.");
        }

        Dictionary<string, ServerConfig> servers = [];
        foreach (IConfigurationSection serverSection in serversSection.GetChildren())
        {
            servers[serverSection.Key] = new ServerConfig
            {
                Name = serverSection.Key,
                Url = GetRequiredValue(serverSection, "url", $"servers.{serverSection.Key}")
            };
        }

        if (servers.Count == 0)
        {
            throw new InvalidOperationException("config.toml must define at least one server.");
        }

        return servers;
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
