namespace Api.Entities;

internal sealed class MessageShardEntity
{
    public long Id { get; init; }
    public string RoomHash { get; set; } = string.Empty;
    public long MessageId { get; set; }
    public int DataShardCount { get; set; }
    public int ParityShardCount { get; set; }
    public int PaddingSize { get; set; }
    public int ShardIndex { get; set; }
    public byte[] ShardData { get; set; } = [];
    public DateTime StoredAtUtc { get; set; }
}
