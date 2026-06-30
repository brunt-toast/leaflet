using Api.Benchmarks.Infrastructure;
using Api.Entities;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.EntityFrameworkCore;
using AppDbContext = Api.Context.AppContext;

namespace Api.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MessageCreateBenchmarks
{
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
        string roomHash = $"create-api-{Interlocked.Increment(ref _messageCounter)}";
        await _cluster!.CreateMessageAsync(0, roomHash);
    }

    [Benchmark(Description = "Message creation (internal DB)")]
    public async Task InternalDbOnly()
    {
        using AppDbContext context = CreateDbContext(_dbPath!);
        EncryptedMessageEntity entity = new()
        {
            RoomHash = $"create-db-{Interlocked.Increment(ref _messageCounter)}",
            SenderPublicKey = "sender-public-key",
            Nonce = "nonce",
            CypherText = "cipher-text",
            Signature = "signature",
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
}
