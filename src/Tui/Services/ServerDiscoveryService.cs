using Core.Responses.Servers;
using Newtonsoft.Json;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class ServerDiscoveryService(HttpClient httpClient)
{
    public async Task<bool> IsDiscoverableAsync(ServerConfig server, CancellationToken cancellationToken)
    {
        string requestUri = $"{server.Url.TrimEnd('/')}/api/servers";

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            GetKnownServersResponse? parsed = JsonConvert.DeserializeObject<GetKnownServersResponse>(responseBody);
            return parsed?.Servers is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }
}
