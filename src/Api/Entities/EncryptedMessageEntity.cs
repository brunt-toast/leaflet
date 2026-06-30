using Core.Types;

namespace Api.Entities;

internal sealed class EncryptedMessageEntity : IEncryptedMessage
{
    public long Id { get; set; }
    public string RoomHash { get; set; } = string.Empty;
    public string SenderPublicKey { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string CypherText { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}
