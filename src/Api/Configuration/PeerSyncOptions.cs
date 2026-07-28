namespace Api.Configuration;

public sealed class PeerSyncOptions
{
    public const string SectionName = "PeerSync";

    public string PublicUrl { get; init; } = string.Empty;
    public int PollIntervalSeconds { get; init; } = 30;
}
