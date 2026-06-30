using Api.Configuration;
using Microsoft.Extensions.Options;

namespace Api.Services;

internal sealed class PeerPollingBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<PeerSyncOptions> options,
    ILogger<PeerPollingBackgroundService> logger) : BackgroundService
{
    private readonly PeerSyncOptions _peerSyncOptions = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_peerSyncOptions.PollIntervalSeconds <= 0)
        {
            logger.LogWarning("Peer polling is disabled because PollIntervalSeconds is not greater than zero.");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_peerSyncOptions.PollIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = serviceScopeFactory.CreateScope();
                IPeerSyncService peerSyncService = scope.ServiceProvider.GetRequiredService<IPeerSyncService>();
                await peerSyncService.PollKnownPeersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Peer polling iteration failed.");
            }
        }
    }
}
