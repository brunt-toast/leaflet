namespace Api.Configuration;

public sealed class ApiLoggingOptions
{
    public const string SectionName = "ApplicationLogging";

    public string SqliteDbPath { get; init; } = "logs/api-logs.db";
    public string MinimumLevel { get; init; } = "Information";
    public string MicrosoftMinimumLevel { get; init; } = "Warning";
}
