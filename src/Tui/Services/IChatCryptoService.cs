using Core.Dto;
using Tui.Configuration;

namespace Tui.Services;

internal interface IChatCryptoService
{
    string ComputeRoomHash(string roomKey);
    EncryptedMessageDto CreateEncryptedMessage(RoomLeafNode room, IdentityConfig identity, string text);
    RenderedMessage TryReadMessage(RoomLeafNode room, EncryptedMessageDto message);
}

internal sealed class RenderedMessage
{
    public required string Timestamp { get; init; }
    public required string Sender { get; init; }
    public required string Body { get; init; }
    public required bool IsVerified { get; init; }
    public required bool IsError { get; init; }
}
