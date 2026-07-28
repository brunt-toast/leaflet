using Api.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Core.Responses.Messages;
using System.Security.Cryptography;
using System.Text;

namespace Api.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MessageRetrievalBenchmarks
{
    private ApiCluster? _cluster;
    private long _knownCounter;
    private long _reconstructCounter;
    private string? _knownRoomHash;
    private long _knownMessageId;
    private string? _reconstructRoomHash;
    private long _reconstructMessageId;

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _cluster = await ApiCluster.StartAsync(nodeCount: 4);
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }
    }

    [IterationSetup(Target = nameof(ApiFullFlowKnownMessage))]
    public void SetupKnownMessageAsync()
    {
        _knownRoomHash = CreateRoomHash($"known-{Interlocked.Increment(ref _knownCounter)}");
        _knownMessageId = _cluster!.CreateMessageAsync(0, _knownRoomHash).GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(ApiFullFlowReconstructedMessage))]
    public void SetupReconstructedMessageAsync()
    {
        _reconstructRoomHash = CreateRoomHash($"reconstruct-{Interlocked.Increment(ref _reconstructCounter)}");
        _reconstructMessageId = _cluster!.CreateMessageAsync(0, _reconstructRoomHash).GetAwaiter().GetResult();
    }

    [Benchmark(Description = "Message retrieval (API full flow - for messages that are known)")]
    public async Task<GetMessagesResponse> ApiFullFlowKnownMessage()
    {
        return await _cluster!.GetMessagesAsync(0, _knownRoomHash!, _knownMessageId);
    }

    [Benchmark(Description = "Message retrieval (API full flow - for messages that must be reconstructed)")]
    public async Task<GetMessagesResponse> ApiFullFlowReconstructedMessage()
    {
        return await _cluster!.GetMessagesAsync(1, _reconstructRoomHash!, _reconstructMessageId);
    }

    private static string CreateRoomHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
