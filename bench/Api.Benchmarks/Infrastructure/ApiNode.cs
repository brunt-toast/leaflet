using System.Diagnostics;
using System.Net.Http.Json;

namespace Api.Benchmarks.Infrastructure;

internal sealed class ApiNode : IAsyncDisposable
{
    private readonly string _apiDllPath;
    private readonly string _dbPath;
    private readonly string _workingDirectory;
    private Process? _process;

    public ApiNode(string apiDllPath, string name, string publicUrl, string dbPath, string workingDirectory)
    {
        _apiDllPath = apiDllPath;
        Name = name;
        PublicUrl = publicUrl;
        _dbPath = dbPath;
        _workingDirectory = workingDirectory;
        Client = new HttpClient
        {
            BaseAddress = new Uri(publicUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public string Name { get; }
    public string PublicUrl { get; }
    public HttpClient Client { get; }

    public async Task StartAsync()
    {
        ProcessStartInfo startInfo = new("dotnet", $"\"{_apiDllPath}\"")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.Environment["ASPNETCORE_URLS"] = PublicUrl;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ConnectionStrings__AppContext"] = $"Data Source={_dbPath}";
        startInfo.Environment["PeerSync__PublicUrl"] = PublicUrl;
        startInfo.Environment["PeerSync__PollIntervalSeconds"] = "0";
        startInfo.Environment["ErasureCoding__DataShards"] = "3";
        startInfo.Environment["ErasureCoding__ParityShards"] = "2";
        startInfo.Environment["RateLimiting__PermitLimit"] = "1000000";
        startInfo.Environment["RateLimiting__WindowSeconds"] = "1";

        _process = new Process
        {
            StartInfo = startInfo,
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to start API node '{Name}'.");
        }

        await WaitForHealthyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
    }

    private async Task WaitForHealthyAsync()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await Client.GetAsync("/api/servers");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"API node '{Name}' did not become healthy.");
    }
}
