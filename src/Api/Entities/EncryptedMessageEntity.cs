using Core.Types;

namespace Api.Entities;

internal sealed class EncryptedMessageEntity : IEncryptedMessage
{
    public long Id { get; init; }
    public string RoomHash { get; init; } = string.Empty;
    public string SenderPublicKey { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string CypherText { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
}
