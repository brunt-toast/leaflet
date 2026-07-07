using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Api.Configuration;

internal sealed class ApiLoggingLevelSwitchUpdater(
    IOptionsMonitor<ApiLoggingOptions> loggingOptionsMonitor,
    ApiLoggingLevelSwitches levelSwitches,
    ILogger<ApiLoggingLevelSwitchUpdater> logger) : IHostedService, IDisposable
{
    private readonly ILogger _logger = logger;
    private IDisposable? _changeSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(loggingOptionsMonitor.CurrentValue);
        _changeSubscription = loggingOptionsMonitor.OnChange(Apply);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
    }

    private void Apply(ApiLoggingOptions options)
    {
        if (!TryParseLevel(options.MinimumLevel, out LogEventLevel minimumLevel))
        {
            _logger.LogWarning("Ignoring invalid API minimum log level '{LogLevel}'.", options.MinimumLevel);
            return;
        }

        if (!TryParseLevel(options.MicrosoftMinimumLevel, out LogEventLevel microsoftMinimumLevel))
        {
            _logger.LogWarning("Ignoring invalid API Microsoft log level '{LogLevel}'.", options.MicrosoftMinimumLevel);
            return;
        }

        levelSwitches.Application.MinimumLevel = minimumLevel;
        levelSwitches.Microsoft.MinimumLevel = microsoftMinimumLevel;
    }

    private static bool TryParseLevel(string value, out LogEventLevel level)
    {
        return Enum.TryParse(value, ignoreCase: true, out level);
    }
}

internal sealed record ApiLoggingLevelSwitches(
    LoggingLevelSwitch Application,
    LoggingLevelSwitch Microsoft);
