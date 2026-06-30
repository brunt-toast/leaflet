namespace Tui.Configuration;

internal sealed class TomlConfigurationSnapshot
{
    public required TuiAppConfig Config { get; init; }
    public required IReadOnlyDictionary<string, string?> FlattenedValues { get; init; }
}
