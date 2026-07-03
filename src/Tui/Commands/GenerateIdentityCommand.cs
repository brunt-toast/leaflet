using System.CommandLine;
using Spectre.Console;
using Tui.Options;
using Tui.Services;
using Tui.Types.Command;

namespace Tui.Commands;

internal class GenerateIdentityCommand : ActionCommand
{
    private readonly AppConfigIoService _appConfigIoService;
    private readonly CompositeIdentityGeneratorService _generator;
    private readonly NameOption _nameOption;

    public GenerateIdentityCommand(
        NameOption nameOption,
        CompositeIdentityGeneratorService generator,
        AppConfigIoService appConfigIoService)
        : base("generate-identity", "Generate a composite identity snippet for config.toml.")
    {
        _appConfigIoService = appConfigIoService;
        _generator = generator;
        _nameOption = nameOption;
        Options.Add(_nameOption);
    }

    protected override async Task<int> ExecuteAction(ParseResult arg, CancellationToken ct)
    {
        string name = arg.GetValue(_nameOption) ?? "NewIdentity";
        await GenerateIdentityAsync(_generator, _appConfigIoService, name, ct);
        return 0;
    }

    private static async Task GenerateIdentityAsync(
        CompositeIdentityGeneratorService generator,
        AppConfigIoService appConfigIoService,
        string name,
        CancellationToken ct)
    {
        CompositeIdentity identity = generator.Generate(name);
        await appConfigIoService.AddIdentityAsync(identity, ct);
        AnsiConsole.MarkupLine($"[green]Generated identity[/] [bold]{Markup.Escape(identity.Name)}[/] and saved it to config.toml.");
    }
}
