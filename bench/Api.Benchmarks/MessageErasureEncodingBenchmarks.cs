using System.Text.Json;
using Api.Configuration;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Core.Dto;
using Witteborn.ReedSolomon;

namespace Api.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MessageErasureEncodingBenchmarks
{
    private readonly ErasureCodingOptions _options = new()
    {
        DataShards = 3,
        ParityShards = 2,
    };

    private readonly EncryptedMessageDto _message = new()
    {
        Id = 42,
        RoomHash = "room-benchmark",
        SenderPublicKey = "sender-public-key",
        Nonce = "nonce",
        CypherText = "cipher-text",
        Signature = "signature",
    };

    [Benchmark(Description = "Message erasure encoding")]
    public (int PaddingSize, int ShardCount, int ShardBytes) Encode()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(_message);
        ReedSolomon reedSolomon = new(_options.DataShards, _options.ParityShards);
        int paddingSize = reedSolomon.GetPaddingSize(payload.Length);
        byte[][] shards = reedSolomon.ManagedEncode(payload);

        return (paddingSize, shards.Length, shards.Sum(shard => shard.Length));
    }
}
