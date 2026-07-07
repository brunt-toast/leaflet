using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Api.Configuration;

internal sealed class ApiLoggingLevelSwitchUpdater : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<ApiLoggingOptions> _loggingOptionsMonitor;
    private readonly ApiLoggingLevelSwitches _levelSwitches;
    private readonly ILogger _logger;
    private IDisposable? _changeSubscription;

    public ApiLoggingLevelSwitchUpdater(
        IOptionsMonitor<ApiLoggingOptions> loggingOptionsMonitor,
        ApiLoggingLevelSwitches levelSwitches,
        ILogger<ApiLoggingLevelSwitchUpdater> logger)
    {
        _loggingOptionsMonitor = loggingOptionsMonitor;
        _levelSwitches = levelSwitches;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(_loggingOptionsMonitor.CurrentValue);
        _changeSubscription = _loggingOptionsMonitor.OnChange(Apply);
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

        _levelSwitches.Application.MinimumLevel = minimumLevel;
        _levelSwitches.Microsoft.MinimumLevel = microsoftMinimumLevel;
    }

    private static bool TryParseLevel(string value, out LogEventLevel level)
    {
        return Enum.TryParse(value, ignoreCase: true, out level);
    }
}

internal sealed record ApiLoggingLevelSwitches
{
    public ApiLoggingLevelSwitches(LoggingLevelSwitch application, LoggingLevelSwitch microsoft)
    {
        Application = application;
        Microsoft = microsoft;
    }

    public LoggingLevelSwitch Application { get; init; }

    public LoggingLevelSwitch Microsoft { get; init; }
}
