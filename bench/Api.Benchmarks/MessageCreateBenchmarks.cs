using Api.Benchmarks.Infrastructure;
using Api.Entities;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppDbContext = Api.Context.AppContext;

namespace Api.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MessageCreateBenchmarks
{
    private static readonly string SampleSenderPublicKey = CreateCompositeEnvelope("sender-public-key-mldsa", "sender-public-key-slhdsa");
    private static readonly string SampleSignature = CreateCompositeEnvelope("signature-mldsa", "signature-slhdsa");
    private static readonly string SampleNonce = Convert.ToBase64String(Enumerable.Range(0, 24).Select(static value => (byte)value).ToArray());
    private ApiCluster? _cluster;
    private string? _dbPath;
    private long _messageCounter;

    [GlobalSetup(Targets = [nameof(ApiFullFlow)])]
    public async Task SetupApiAsync()
    {
        _cluster = await ApiCluster.StartAsync(nodeCount: 4);
    }

    [GlobalCleanup(Targets = [nameof(ApiFullFlow)])]
    public async Task CleanupApiAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }
    }

    [GlobalSetup(Targets = [nameof(InternalDbOnly)])]
    public void SetupDb()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"messaging-2-db-bench-{Guid.NewGuid():N}")).FullName;
        _dbPath = Path.Combine(root, "messages.db");

        using AppDbContext context = CreateDbContext(_dbPath);
        context.Database.EnsureCreated();
    }

    [GlobalCleanup(Targets = [nameof(InternalDbOnly)])]
    public void CleanupDb()
    {
        if (_dbPath is null)
        {
            return;
        }

        try
        {
            File.Delete(_dbPath);
            string? directory = Path.GetDirectoryName(_dbPath);
            if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Benchmark(Description = "Message creation (API - full flow)")]
    public async Task ApiFullFlow()
    {
        string roomHash = CreateRoomHash($"create-api-{Interlocked.Increment(ref _messageCounter)}");
        await _cluster!.CreateMessageAsync(0, roomHash);
    }

    [Benchmark(Description = "Message creation (internal DB)")]
    public async Task InternalDbOnly()
    {
        using AppDbContext context = CreateDbContext(_dbPath!);
        EncryptedMessageEntity entity = new()
        {
            RoomHash = CreateRoomHash($"create-db-{Interlocked.Increment(ref _messageCounter)}"),
            SenderPublicKey = SampleSenderPublicKey,
            Nonce = SampleNonce,
            CypherText = CreateCipherText("cipher-text"),
            Signature = SampleSignature,
        };

        await context.EncryptedMessages.AddAsync(entity);
        await context.SaveChangesAsync();
    }

    private static AppDbContext CreateDbContext(string dbPath)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        return new AppDbContext(options);
    }

    private static string CreateRoomHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string CreateCipherText(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string CreateCompositeEnvelope(string firstValue, string secondValue)
    {
        return JsonSerializer.Serialize(new
        {
            mldsa = Convert.ToBase64String(Encoding.UTF8.GetBytes(firstValue)),
            slhdsa = Convert.ToBase64String(Encoding.UTF8.GetBytes(secondValue))
        });
    }
}
