using Core.Responses.Servers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class ServerDiscoveryService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ServerDiscoveryService> _logger;

    public ServerDiscoveryService(
        HttpClient httpClient,
        ILogger<ServerDiscoveryService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> IsDiscoverableAsync(ServerConfig server, CancellationToken cancellationToken)
    {
        string requestUri = $"{server.Url.TrimEnd('/')}/api/servers";

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Server {ServerUrl} discovery check returned status code {StatusCode}.", server.Url, response.StatusCode);
                return false;
            }

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            GetKnownServersResponse? parsed = JsonConvert.DeserializeObject<GetKnownServersResponse>(responseBody);
            return parsed?.Servers is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Server {ServerUrl} discovery check failed.", server.Url);
            return false;
        }
    }
}
