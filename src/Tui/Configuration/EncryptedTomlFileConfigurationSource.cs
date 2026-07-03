using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Tomlyn.Extensions.Configuration;
using Tui.Extensions.System;
using Tui.Services;

namespace Tui.Configuration;

internal sealed class EncryptedTomlFileConfigurationSource : IConfigurationSource
{
    public EncryptedTomlFileConfigurationSource(string filePath)
    {
        FilePath = filePath;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new EncryptedTomlFileConfigurationProvider(this);

    internal string FilePath { get; }
}

internal sealed class EncryptedTomlFileConfigurationProvider : ConfigurationProvider
{
    private readonly EncryptedTomlFileConfigurationSource _source;

    public EncryptedTomlFileConfigurationProvider(EncryptedTomlFileConfigurationSource source)
    {
        _source = source;
    }

    public override void Load()
    {
        AppConfigIoService appConfigIoService = new(
            _source.FilePath,
            new PasswordService(AnsiConsole.Console));
        string content = appConfigIoService.LoadConfig();
        IConfigurationRoot tomlConfig = new ConfigurationBuilder()
            .AddTomlStream(content.ToStream())
            .Build();

        Data = tomlConfig
            .AsEnumerable()
            .Where(kvp => kvp.Value is not null)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);
    }
}

internal static class EncryptedTomlFileConfigurationExtensions
{
    extension(IConfigurationBuilder builder)
    {
        public IConfigurationBuilder AddEncryptedTomlFile(
            string filePath,
            Action<EncryptedTomlFileConfigurationSource>? configure = null)
        {
            EncryptedTomlFileConfigurationSource source = new(filePath);
            configure?.Invoke(source);
            return builder.Add(source);
        }
    }
}
