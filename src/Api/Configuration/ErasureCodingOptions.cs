namespace Api.Configuration;

public sealed class ErasureCodingOptions
{
    public const string SectionName = "ErasureCoding";

    public int DataShards { get; init; } = 2;
    public int ParityShards { get; init; } = 1;
    public int RepairIntervalSeconds { get; init; } = 60;
    public int RepairBatchSize { get; init; } = 50;
}
