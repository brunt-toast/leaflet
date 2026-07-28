namespace Core.Types;

public interface IEncryptedMessage
{
    long Id { get; }
    string RoomHash { get; }
    string SenderPublicKey { get; }
    string Nonce { get; }
    string CypherText { get; }
    string Signature { get; }
}
