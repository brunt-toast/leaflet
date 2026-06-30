namespace Tui.Configuration;

internal sealed class TuiAppConfig
{
    public required TuiCoreConfig Core { get; init; }
    public required IReadOnlyDictionary<string, IdentityConfig> Identities { get; init; }
    public required IReadOnlyDictionary<string, ServerConfig> Servers { get; init; }
    public required RoomGroupNode RoomsRoot { get; init; }
}

internal sealed class TuiCoreConfig
{
    public int HistoryCount { get; init; } = 50;
    public int RefreshIntervalSeconds { get; init; } = 10;
}

internal sealed class IdentityConfig
{
    public required string Name { get; init; }
    public required string PublicKey { get; init; }
    public required string PrivateKey { get; init; }
}

internal sealed class ServerConfig
{
    public required string Name { get; init; }
    public required string Url { get; init; }
}

internal abstract class RoomTreeNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}

internal sealed class RoomGroupNode : RoomTreeNode
{
    public required IReadOnlyList<RoomTreeNode> Children { get; init; }
}

internal sealed class RoomLeafNode : RoomTreeNode
{
    public required string Key { get; init; }
    public required string IdentityName { get; init; }
}
