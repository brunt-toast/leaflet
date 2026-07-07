using System.Text;
using Core.Dto;
using Core.Requests.Messages;
using Core.Responses.Messages;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class MessagingApiClientService(
    HttpClient httpClient,
    ILogger<MessagingApiClientService> logger)
{
    private readonly ILogger _logger = logger;

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
        IReadOnlyList<EncryptedMessageDto> messages = parsed?.Messages ?? [];
        _logger.LogInformation(
            "Retrieved {MessageCount} messages for room {RoomHash} from {ServerUrl}.",
            messages.Count,
            roomHash,
            server.Url);
        return messages;
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
        _logger.LogInformation("Sending message for room {RoomHash} to {ServerUrl}.", message.RoomHash, server.Url);
        using HttpResponseMessage response = await httpClient.PostAsync(requestUri, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Sent message for room {RoomHash} to {ServerUrl}.", message.RoomHash, server.Url);
    }
}
