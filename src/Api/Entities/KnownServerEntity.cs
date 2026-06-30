namespace Api.Entities;

internal sealed class KnownServerEntity
{
    public long Id { get; init; }
    public string Url { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}
