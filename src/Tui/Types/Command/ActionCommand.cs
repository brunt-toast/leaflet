using System.CommandLine;

namespace Tui.Types.Command;

internal abstract class ActionCommand : System.CommandLine.Command
{
    protected ActionCommand(string name, string? description = null) : base(name, description)
    {
        SetAction(ExecuteAction);
    }

    protected abstract Task<int> ExecuteAction(ParseResult arg, CancellationToken ct);
}
