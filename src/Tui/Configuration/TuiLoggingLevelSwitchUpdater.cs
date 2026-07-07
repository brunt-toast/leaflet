using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Tui.Configuration;

internal sealed class TuiLoggingLevelSwitchUpdater : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<TuiLoggingConfig> _loggingConfigMonitor;
    private readonly TuiLoggingLevelSwitches _levelSwitches;
    private readonly ILogger _logger;
    private IDisposable? _changeSubscription;

    public TuiLoggingLevelSwitchUpdater(
        IOptionsMonitor<TuiLoggingConfig> loggingConfigMonitor,
        TuiLoggingLevelSwitches levelSwitches,
        ILogger<TuiLoggingLevelSwitchUpdater> logger)
    {
        _loggingConfigMonitor = loggingConfigMonitor;
        _levelSwitches = levelSwitches;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(_loggingConfigMonitor.CurrentValue);
        _changeSubscription = _loggingConfigMonitor.OnChange(Apply);
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

        _levelSwitches.Application.MinimumLevel = minimumLevel;
        _levelSwitches.Microsoft.MinimumLevel = microsoftMinimumLevel;
    }

    private static bool TryParseLevel(string value, out LogEventLevel level)
    {
        return Enum.TryParse(value, ignoreCase: true, out level);
    }
}

internal sealed record TuiLoggingLevelSwitches
{
    public TuiLoggingLevelSwitches(LoggingLevelSwitch application, LoggingLevelSwitch microsoft)
    {
        Application = application;
        Microsoft = microsoft;
    }

    public LoggingLevelSwitch Application { get; init; }

    public LoggingLevelSwitch Microsoft { get; init; }
}
