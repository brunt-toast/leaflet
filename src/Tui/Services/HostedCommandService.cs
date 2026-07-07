using System.CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tui.Services;

internal sealed class HostedCommandService : IHostedService
{
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly CommandLineInvocation _invocation;
    private readonly ILogger _logger;
    private readonly RootCommand _rootCommand;

    public HostedCommandService(
        RootCommand rootCommand,
        CommandLineInvocation invocation,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<HostedCommandService> logger)
    {
        _rootCommand = rootCommand;
        _invocation = invocation;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;
    }

    public int ExitCode { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RunAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting command invocation: {Arguments}", string.Join(' ', _invocation.Args));
            ExitCode = await _rootCommand.Parse(_invocation.Args).InvokeAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Command invocation completed with exit code {ExitCode}.", ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command invocation failed unexpectedly.");
            ExitCode = 1;
        }
        finally
        {
            _hostApplicationLifetime.StopApplication();
        }
    }
}

internal sealed record CommandLineInvocation(string[] Args);
