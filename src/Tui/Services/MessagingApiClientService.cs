using System.Text;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;
using Newtonsoft.Json;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class MessagingApiClientService(HttpClient httpClient)
{
    public async Task<IReadOnlyList<EncryptedMessageDto>> GetMessagesAsync(
        ServerConfig server,
        string roomHash,
        int numberToFetch,
        CancellationToken cancellationToken)
    {
        string requestUri =
            $"{server.Url.TrimEnd('/')}/api/messages?roomHash={Uri.EscapeDataString(roomHash)}&maxId={long.MaxValue}&numberToFetch={numberToFetch}";

        using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        GetMessagesResponse? parsed = JsonConvert.DeserializeObject<GetMessagesResponse>(responseBody);
        return parsed?.Messages ?? [];
    }

    public async Task SendMessageAsync(ServerConfig server, EncryptedMessageDto message, CancellationToken cancellationToken)
    {
        string requestUri = $"{server.Url.TrimEnd('/')}/api/messages";
        CreateMessagesRequest request = new()
        {
            Messages = [message]
        };

        string requestBody = JsonConvert.SerializeObject(request);
        using StringContent content = new(requestBody, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await httpClient.PostAsync(requestUri, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
