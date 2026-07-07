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
    internal bool ReloadOnChange { get; init; } = true;
    internal int ReloadDelayMilliseconds { get; init; } = 500;
}

internal sealed class EncryptedTomlFileConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly EncryptedTomlFileConfigurationSource _source;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _reloadTimer;
    private readonly object _reloadLock = new();
    private bool _disposed;

    public EncryptedTomlFileConfigurationProvider(EncryptedTomlFileConfigurationSource source)
    {
        _source = source;
        if (_source.ReloadOnChange)
        {
            string fullPath = Path.GetFullPath(_source.FilePath);
            string? directoryPath = Path.GetDirectoryName(fullPath);
            string fileName = Path.GetFileName(fullPath);

            if (!string.IsNullOrWhiteSpace(directoryPath) && !string.IsNullOrWhiteSpace(fileName))
            {
                Directory.CreateDirectory(directoryPath);
                _reloadTimer = new Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
                _watcher = new FileSystemWatcher(directoryPath, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.Size
                };
                _watcher.Changed += OnWatchedFileChanged;
                _watcher.Created += OnWatchedFileChanged;
                _watcher.Renamed += OnWatchedFileChanged;
                _watcher.EnableRaisingEvents = true;
            }
        }
    }

    public override void Load()
    {
        Data = LoadConfigurationData();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatchedFileChanged;
            _watcher.Created -= OnWatchedFileChanged;
            _watcher.Renamed -= OnWatchedFileChanged;
            _watcher.Dispose();
        }

        _reloadTimer?.Dispose();
    }

    private IDictionary<string, string?> LoadConfigurationData()
    {
        AppConfigIoService appConfigIoService = new(
            _source.FilePath,
            new PasswordService(AnsiConsole.Console));
        string content = appConfigIoService.LoadConfig();
        IConfigurationRoot tomlConfig = new ConfigurationBuilder()
            .AddTomlStream(content.ToStream())
            .Build();

        return tomlConfig
            .AsEnumerable()
            .Where(kvp => kvp.Value is not null)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs args)
    {
        _reloadTimer?.Change(_source.ReloadDelayMilliseconds, Timeout.Infinite);
    }

    private void ReloadFromDisk()
    {
        lock (_reloadLock)
        {
            try
            {
                Data = LoadConfigurationData();
                OnReload();
            }
            catch
            {
            }
        }
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
