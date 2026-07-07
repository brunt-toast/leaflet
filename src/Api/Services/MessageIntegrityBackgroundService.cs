using Api.Configuration;
using Microsoft.Extensions.Options;

namespace Api.Services;

internal sealed class MessageIntegrityBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ErasureCodingOptions _erasureCodingOptions;
    private readonly ILogger<MessageIntegrityBackgroundService> _logger;

    public MessageIntegrityBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<ErasureCodingOptions> options,
        ILogger<MessageIntegrityBackgroundService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _erasureCodingOptions = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_erasureCodingOptions.RepairIntervalSeconds <= 0)
        {
            _logger.LogWarning("Message integrity repair is disabled because RepairIntervalSeconds is not greater than zero.");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_erasureCodingOptions.RepairIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = _serviceScopeFactory.CreateScope();
                IMessageErasureCodingService erasureCodingService = scope.ServiceProvider.GetRequiredService<IMessageErasureCodingService>();
                await erasureCodingService.RepairShardIntegrityAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Message integrity repair iteration failed.");
            }
        }
    }
}
