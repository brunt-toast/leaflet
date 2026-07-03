using System.CommandLine;

namespace Tui.Options;

internal class NameOption : Option<string?>
{
    public NameOption() : base("--name")
    {
        Description = "Identity name to generate.";
    }
}
