using Api.Configuration;
using Microsoft.Extensions.Options;

namespace Api.Services;

internal sealed class MessageIntegrityBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<ErasureCodingOptions> options,
    ILogger<MessageIntegrityBackgroundService> logger) : BackgroundService
{
    private readonly ErasureCodingOptions _erasureCodingOptions = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_erasureCodingOptions.RepairIntervalSeconds <= 0)
        {
            logger.LogWarning("Message integrity repair is disabled because RepairIntervalSeconds is not greater than zero.");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_erasureCodingOptions.RepairIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = serviceScopeFactory.CreateScope();
                IMessageErasureCodingService erasureCodingService = scope.ServiceProvider.GetRequiredService<IMessageErasureCodingService>();
                await erasureCodingService.RepairShardIntegrityAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message integrity repair iteration failed.");
            }
        }
    }
}
