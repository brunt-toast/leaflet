namespace Api.Configuration;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int PermitLimit { get; init; } = 20;
    public int WindowSeconds { get; init; } = 60;
}
