using Tomlyn;
using Tomlyn.Model;

namespace Tui.Configuration;

internal sealed class TomlConfigLoader
{
    public TomlConfigurationSnapshot Load(string path)
    {
        return LoadFromContent(path, File.ReadAllText(path));
    }

    public TomlConfigurationSnapshot LoadFromContent(string path, string content)
    {
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(content)
            ?? throw new InvalidOperationException($"Could not parse TOML config at '{path}'.");

        Dictionary<string, string?> flat = [];

        TuiCoreConfig core = ParseCore(root, flat);
        IReadOnlyDictionary<string, IdentityConfig> identities = ParseIdentities(root, flat);
        IReadOnlyDictionary<string, ServerConfig> servers = ParseServers(root, flat);
        RoomGroupNode roomsRoot = ParseRooms(root, flat);

        return new TomlConfigurationSnapshot
        {
            Config = new TuiAppConfig
            {
                Core = core,
                Identities = identities,
                Servers = servers,
                RoomsRoot = roomsRoot,
            },
            FlattenedValues = flat,
        };
    }

    private static TuiCoreConfig ParseCore(TomlTable root, IDictionary<string, string?> flat)
    {
        int historyCount = 50;
        int refreshIntervalSeconds = 10;
        if (TryGetTable(root, "core", out TomlTable? coreTable) &&
            coreTable is not null)
        {
            if (coreTable.TryGetValue("history_count", out object? historyCountValue) &&
                historyCountValue is long historyCountLong)
            {
                historyCount = checked((int)historyCountLong);
            }

            if (coreTable.TryGetValue("refresh_interval_seconds", out object? refreshIntervalValue) &&
                refreshIntervalValue is long refreshIntervalLong)
            {
                refreshIntervalSeconds = checked((int)refreshIntervalLong);
            }
        }

        flat["core:history_count"] = historyCount.ToString();
        flat["core:refresh_interval_seconds"] = refreshIntervalSeconds.ToString();
        return new TuiCoreConfig
        {
            HistoryCount = historyCount,
            RefreshIntervalSeconds = Math.Max(1, refreshIntervalSeconds),
        };
    }

    private static IReadOnlyDictionary<string, IdentityConfig> ParseIdentities(TomlTable root, IDictionary<string, string?> flat)
    {
        if (!TryGetTable(root, "identities", out TomlTable? identitiesTable))
        {
            throw new InvalidOperationException("config.toml must contain an [identities] section.");
        }

        Dictionary<string, IdentityConfig> identities = [];
        TomlTable identityEntries = identitiesTable!;
        foreach ((string identityName, object? value) in identityEntries)
        {
            if (value is not TomlTable identityTable)
            {
                continue;
            }

            IdentityConfig identity = new()
            {
                Name = GetRequiredString(identityTable, "name", $"identities.{identityName}"),
                PublicKey = GetRequiredString(identityTable, "public_key", $"identities.{identityName}"),
                PrivateKey = GetRequiredString(identityTable, "private_key", $"identities.{identityName}"),
            };

            identities[identityName] = identity;
            flat[$"identities:{identityName}:name"] = identity.Name;
            flat[$"identities:{identityName}:public_key"] = identity.PublicKey;
            flat[$"identities:{identityName}:private_key"] = identity.PrivateKey;
        }

        if (identities.Count == 0)
        {
            throw new InvalidOperationException("config.toml must define at least one identity.");
        }

        return identities;
    }

    private static IReadOnlyDictionary<string, ServerConfig> ParseServers(TomlTable root, IDictionary<string, string?> flat)
    {
        if (!TryGetTable(root, "servers", out TomlTable? serversTable))
        {
            throw new InvalidOperationException("config.toml must contain a [servers] section.");
        }

        Dictionary<string, ServerConfig> servers = [];
        TomlTable serverEntries = serversTable!;
        foreach ((string serverName, object? value) in serverEntries)
        {
            if (value is not TomlTable serverTable)
            {
                continue;
            }

            ServerConfig server = new()
            {
                Name = serverName,
                Url = GetRequiredString(serverTable, "url", $"servers.{serverName}"),
            };

            servers[serverName] = server;
            flat[$"servers:{serverName}:url"] = server.Url;
        }

        if (servers.Count == 0)
        {
            throw new InvalidOperationException("config.toml must define at least one server.");
        }

        return servers;
    }

    private static RoomGroupNode ParseRooms(TomlTable root, IDictionary<string, string?> flat)
    {
        if (!TryGetTable(root, "rooms", out TomlTable? roomsTable))
        {
            throw new InvalidOperationException("config.toml must contain a [rooms] section.");
        }

        List<RoomTreeNode> children = [];
        TomlTable roomEntries = roomsTable!;
        foreach ((string childName, object? childValue) in roomEntries)
        {
            if (childValue is TomlTable childTable)
            {
                children.Add(ParseRoomNode(childName, "rooms", childTable, flat));
            }
        }

        return new RoomGroupNode
        {
            Name = "rooms",
            Path = "rooms",
            Children = children.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static RoomTreeNode ParseRoomNode(string name, string parentPath, TomlTable table, IDictionary<string, string?> flat)
    {
        string fullPath = $"{parentPath}.{name}";
        bool isRoom = table.ContainsKey("key");

        if (isRoom)
        {
            string key = GetRequiredString(table, "key", fullPath);
            string identityName = GetRequiredString(table, "identity", fullPath);
            flat[$"{fullPath.Replace('.', ':')}:key"] = key;
            flat[$"{fullPath.Replace('.', ':')}:identity"] = identityName;

            return new RoomLeafNode
            {
                Name = name,
                Path = fullPath,
                Key = key,
                IdentityName = identityName,
            };
        }

        List<RoomTreeNode> children = [];
        foreach ((string childName, object? childValue) in table)
        {
            if (childValue is TomlTable childTable)
            {
                children.Add(ParseRoomNode(childName, fullPath, childTable, flat));
            }
        }

        return new RoomGroupNode
        {
            Name = name,
            Path = fullPath,
            Children = children.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static bool TryGetTable(TomlTable root, string key, out TomlTable? table)
    {
        if (root.TryGetValue(key, out object? value) && value is TomlTable result)
        {
            table = result;
            return true;
        }

        table = null;
        return false;
    }

    private static string GetRequiredString(TomlTable table, string key, string path)
    {
        if (table.TryGetValue(key, out object? value) && value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
        {
            return stringValue;
        }

        throw new InvalidOperationException($"Missing required string '{key}' at [{path}].");
    }
}
