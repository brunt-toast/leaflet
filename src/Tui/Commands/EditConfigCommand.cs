using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;
using Tui.Services;
using Tui.Types.Command;

namespace Tui.Commands;

internal sealed class EditConfigCommand : ActionCommand
{
    private readonly AppConfigIoService _appConfigIoService;
    private readonly PasswordService _passwordService;

    public EditConfigCommand(AppConfigIoService appConfigIoService, PasswordService passwordService)
        : base("edit-config", "Edit config.toml.")
    {
        _appConfigIoService = appConfigIoService;
        _passwordService = passwordService;
    }

    protected override async Task<int> ExecuteAction(ParseResult arg, CancellationToken ct)
    {
        string password = _passwordService.GetPassword();
        string decryptedConfig = await _appConfigIoService.ReadAsync(password, ct);

        string tempFilePath = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFilePath, decryptedConfig, ct);

        try
        {
            await RunEditorAsync(tempFilePath, ct);
            decryptedConfig = await File.ReadAllTextAsync(tempFilePath, ct);
            await _appConfigIoService.WriteAsync(decryptedConfig, password, ct);
        }
        finally
        {
            File.Delete(tempFilePath);
        }

        AnsiConsole.MarkupLine("[green]Saved config.[/]");
        return 0;
    }

    private static async Task RunEditorAsync(string tempFilePath, CancellationToken ct)
    {
        string editor = Environment.GetEnvironmentVariable("VISUAL")
            ?? Environment.GetEnvironmentVariable("EDITOR")
            ?? (OperatingSystem.IsWindows() ? "notepad" : "nano");

        ProcessStartInfo startInfo = new()
        {
            FileName = editor,
            Arguments = $"\"{tempFilePath}\"",
            UseShellExecute = true
        };

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        await process.WaitForExitAsync(cancellationToken: ct);
    }
}
