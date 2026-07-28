using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace Api.Benchmarks;

internal sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.ShortRun);
        AddDiagnoser(MemoryDiagnoser.Default);
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
