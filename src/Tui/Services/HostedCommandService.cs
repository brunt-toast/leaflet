using System.CommandLine;
using Microsoft.Extensions.Hosting;

namespace Tui.Services;

internal sealed class HostedCommandService : IHostedService
{
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly CommandLineInvocation _invocation;
    private readonly RootCommand _rootCommand;

    public HostedCommandService(
        RootCommand rootCommand,
        CommandLineInvocation invocation,
        IHostApplicationLifetime hostApplicationLifetime)
    {
        _rootCommand = rootCommand;
        _invocation = invocation;
        _hostApplicationLifetime = hostApplicationLifetime;
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
            ExitCode = await _rootCommand.Parse(_invocation.Args).InvokeAsync(cancellationToken: cancellationToken);
        }
        finally
        {
            _hostApplicationLifetime.StopApplication();
        }
    }
}

internal sealed record CommandLineInvocation(string[] Args);
