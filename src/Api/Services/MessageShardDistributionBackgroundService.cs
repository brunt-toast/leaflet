using Core.Dto;

namespace Api.Services;

internal sealed class MessageShardDistributionBackgroundService : BackgroundService
{
    private readonly IMessageShardDistributionQueue _queue;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<MessageShardDistributionBackgroundService> _logger;

    public MessageShardDistributionBackgroundService(
        IMessageShardDistributionQueue queue,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<MessageShardDistributionBackgroundService> logger)
    {
        _queue = queue;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (IReadOnlyCollection<EncryptedMessageDto> messages in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = _serviceScopeFactory.CreateScope();
                IMessageErasureCodingService erasureCodingService = scope.ServiceProvider.GetRequiredService<IMessageErasureCodingService>();
                await erasureCodingService.DistributeMessageShardsAsync(messages, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background shard distribution failed for a queued message batch.");
            }
        }
    }
}
