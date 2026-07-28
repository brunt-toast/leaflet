namespace Api.Dto;

public sealed class MessageShardDto
{
    public required string RoomHash { get; init; } = string.Empty;
    public required long MessageId { get; init; }
    public required int DataShardCount { get; init; }
    public required int ParityShardCount { get; init; }
    public required int PaddingSize { get; init; }
    public required int ShardIndex { get; init; }
    public required byte[] ShardData { get; init; } = [];
}
