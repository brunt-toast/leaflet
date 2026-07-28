using Spectre.Console;

namespace Tui.Services;

internal sealed class PasswordService
{
    private static readonly Dictionary<string, string> s_cache = [];
    private readonly IAnsiConsole _console;

    public PasswordService(IAnsiConsole console)
    {
        _console = console;
    }

    public string GetPassword(string prompt = "Password: ", string cacheKey = "default")
    {
        if (s_cache.TryGetValue(cacheKey, out string? ret))
        {
            return ret;
        }

        ret = _console.Prompt(
            new TextPrompt<string>(prompt)
                .PromptStyle("green")
                .Secret()
                .Validate(input => string.IsNullOrWhiteSpace(input)
                    ? ValidationResult.Error("[red]Password cannot be empty.[/]")
                    : ValidationResult.Success()));

        s_cache.Add(cacheKey, ret);
        return ret;
    }

    public string GetConfirmedPassword(
        string prompt = "New password: ",
        string confirmPrompt = "Confirm password: ")
    {
        string password = GetPassword(prompt, Guid.NewGuid().ToString());
        string confirmedPassword = GetPassword(confirmPrompt);

        if (!string.Equals(password, confirmedPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Passwords did not match.");
        }

        return password;
    }
}
