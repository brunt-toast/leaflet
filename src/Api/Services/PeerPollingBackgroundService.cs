using Api.Configuration;
using Microsoft.Extensions.Options;

namespace Api.Services;

internal sealed class PeerPollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly PeerSyncOptions _peerSyncOptions;
    private readonly ILogger<PeerPollingBackgroundService> _logger;

    public PeerPollingBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<PeerSyncOptions> options,
        ILogger<PeerPollingBackgroundService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _peerSyncOptions = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_peerSyncOptions.PollIntervalSeconds <= 0)
        {
            _logger.LogWarning("Peer polling is disabled because PollIntervalSeconds is not greater than zero.");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(_peerSyncOptions.PollIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = _serviceScopeFactory.CreateScope();
                IPeerSyncService peerSyncService = scope.ServiceProvider.GetRequiredService<IPeerSyncService>();
                await peerSyncService.PollKnownPeersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Peer polling iteration failed.");
            }
        }
    }
}
