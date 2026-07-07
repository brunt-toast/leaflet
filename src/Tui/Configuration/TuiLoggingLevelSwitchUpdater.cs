using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Tui.Configuration;

internal sealed class TuiLoggingLevelSwitchUpdater(
    IOptionsMonitor<TuiLoggingConfig> loggingConfigMonitor,
    TuiLoggingLevelSwitches levelSwitches,
    ILogger<TuiLoggingLevelSwitchUpdater> logger) : IHostedService, IDisposable
{
    private readonly ILogger _logger = logger;
    private IDisposable? _changeSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(loggingConfigMonitor.CurrentValue);
        _changeSubscription = loggingConfigMonitor.OnChange(Apply);
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

    private void Apply(TuiLoggingConfig loggingConfig)
    {
        if (!TryParseLevel(loggingConfig.MinimumLevel, out LogEventLevel minimumLevel))
        {
            _logger.LogWarning("Ignoring invalid TUI minimum log level '{LogLevel}'.", loggingConfig.MinimumLevel);
            return;
        }

        if (!TryParseLevel(loggingConfig.MicrosoftMinimumLevel, out LogEventLevel microsoftMinimumLevel))
        {
            _logger.LogWarning("Ignoring invalid TUI Microsoft log level '{LogLevel}'.", loggingConfig.MicrosoftMinimumLevel);
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

internal sealed record TuiLoggingLevelSwitches(
    LoggingLevelSwitch Application,
    LoggingLevelSwitch Microsoft);
