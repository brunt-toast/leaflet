using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Tui.Services;

namespace Tui.Commands;

internal sealed class TuiRootCommand : RootCommand
{
    public TuiRootCommand(
        IServiceProvider serviceProvider,
        GenerateIdentityCommand generateIdentityCommand,
        EditConfigCommand editConfigCommand)
        : base("Messaging TUI")
    {
        SetAction(async (_, cancellationToken) =>
        {
            TuiApplicationService app = serviceProvider.GetRequiredService<TuiApplicationService>();
            await app.RunAsync(cancellationToken);
            return 0;
        });

        Subcommands.Add(generateIdentityCommand);
        Subcommands.Add(editConfigCommand);
    }
}
