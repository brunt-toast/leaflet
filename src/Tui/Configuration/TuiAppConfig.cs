namespace Tui.Configuration;

internal sealed class TuiAppConfig
{
    public TuiCoreConfig Core { get; set; } = new();
    public IReadOnlyDictionary<string, IdentityConfig> Identities { get; set; } = new Dictionary<string, IdentityConfig>();
    public ServerClusterConfig Servers { get; set; } = new();
    public RoomGroupNode RoomsRoot { get; set; } = new()
    {
        Name = "rooms",
        Path = "rooms",
        Children = []
    };
}

internal sealed class TuiCoreConfig
{
    public int HistoryCount { get; init; } = 50;
    public int RefreshIntervalSeconds { get; init; } = 10;
}

internal sealed class IdentityConfig
{
    public string Name { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;
}

internal sealed class ServerConfig
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

internal sealed class ServerClusterConfig
{
    public ServerConfig Main { get; init; } = new();
    public IReadOnlyList<ServerConfig> Backups { get; init; } = [];
}

internal abstract class RoomTreeNode
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

internal sealed class RoomGroupNode : RoomTreeNode
{
    public IReadOnlyList<RoomTreeNode> Children { get; init; } = [];
}

internal sealed class RoomLeafNode : RoomTreeNode
{
    public string Key { get; init; } = string.Empty;
    public string IdentityName { get; init; } = string.Empty;
}
