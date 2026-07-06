using System.Text.Json;
using Api.Configuration;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Core.Dto;
using System.Text;
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
        RoomHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("room-benchmark"))).ToLowerInvariant(),
        SenderPublicKey = JsonSerializer.Serialize(new
        {
            mldsa = Convert.ToBase64String(Encoding.UTF8.GetBytes("sender-public-key-mldsa")),
            slhdsa = Convert.ToBase64String(Encoding.UTF8.GetBytes("sender-public-key-slhdsa"))
        }),
        Nonce = Convert.ToBase64String(Enumerable.Range(0, 24).Select(static value => (byte)value).ToArray()),
        CypherText = Convert.ToBase64String(Encoding.UTF8.GetBytes("cipher-text")),
        Signature = JsonSerializer.Serialize(new
        {
            mldsa = Convert.ToBase64String(Encoding.UTF8.GetBytes("signature-mldsa")),
            slhdsa = Convert.ToBase64String(Encoding.UTF8.GetBytes("signature-slhdsa"))
        }),
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
