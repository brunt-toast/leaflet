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
    public string? LocalId { get; init; }
    public required DateTimeOffset SentAtUtc { get; init; }
    public required string Sender { get; init; }
    public required string SenderKeyHash { get; init; }
    public required string Body { get; init; }
    public required bool IsVerified { get; init; }
    public required bool IsError { get; init; }
    public bool IsPending { get; init; }
    public bool DeliveryFailed { get; init; }
}
