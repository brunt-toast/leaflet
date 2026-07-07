using Core.Responses.Servers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class ServerDiscoveryService(
    HttpClient httpClient,
    ILogger<ServerDiscoveryService> logger)
{
    private readonly ILogger _logger = logger;

    public async Task<bool> IsDiscoverableAsync(ServerConfig server, CancellationToken cancellationToken)
    {
        string requestUri = $"{server.Url.TrimEnd('/')}/api/servers";

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);
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
