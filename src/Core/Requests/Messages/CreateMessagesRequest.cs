using Core.Dto;

namespace Core.Requests.Messages;

public sealed class CreateMessagesRequest
{
    public required EncryptedMessageDto[] Messages { get; init; } = [];
}
