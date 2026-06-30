namespace Api.Configuration;

public sealed class ErasureCodingOptions
{
    public const string SectionName = "ErasureCoding";

    public int DataShards { get; init; } = 2;
    public int ParityShards { get; init; } = 1;
}
