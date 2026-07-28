using Core.Dto;

namespace Core.Responses.Messages;

public sealed class GetMessagesResponse
{
    public required EncryptedMessageDto[] Messages { get; init; }
}
