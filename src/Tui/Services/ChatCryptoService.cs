using System.Text;
using Core.Dto;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Sodium;
using Tui.Configuration;

namespace Tui.Services;

internal sealed class ChatCryptoService : IChatCryptoService
{
    private const int HashBytes = 32;
    private const int XChaChaKeyBytes = 32;

    public string ComputeRoomHash(string roomKey)
    {
        byte[] roomKeyBytes = Encoding.UTF8.GetBytes(roomKey);
        byte[] hashBytes = Shake256(roomKeyBytes, HashBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public EncryptedMessageDto CreateEncryptedMessage(RoomLeafNode room, IdentityConfig identity, string text)
    {
        byte[] roomKey = DeriveEncryptionKey(room.Key);
        byte[] nonce = SecretAeadXChaCha20Poly1305.GenerateNonce();
        string encryptedSenderPublicKey = EncryptSenderPublicKey(identity.PublicKey, roomKey);
        RoomMessageEnvelope envelope = new()
        {
            Content = new RoomMessageContent
            {
                Text = text
            },
            Metadata = new RoomMessageMetadata
            {
                SenderName = identity.Name,
                SentAtUtc = DateTimeOffset.UtcNow
            }
        };

        byte[] plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope));
        byte[] cipherText = SecretAeadXChaCha20Poly1305.Encrypt(plaintext, nonce, roomKey, []);
        string roomHash = ComputeRoomHash(room.Key);
        byte[] signaturePayload = BuildSignaturePayload(roomHash, nonce, cipherText, encryptedSenderPublicKey);
        string signature = Sign(signaturePayload, identity.PrivateKey);

        return new EncryptedMessageDto
        {
            Id = 0,
            RoomHash = roomHash,
            SenderPublicKey = encryptedSenderPublicKey,
            Nonce = Convert.ToBase64String(nonce),
            CypherText = Convert.ToBase64String(cipherText),
            Signature = signature
        };
    }

    public RenderedMessage TryReadMessage(RoomLeafNode room, EncryptedMessageDto message)
    {
        try
        {
            byte[] roomKey = DeriveEncryptionKey(room.Key);
            byte[] nonce = Convert.FromBase64String(message.Nonce);
            byte[] cipherText = Convert.FromBase64String(message.CypherText);
            string senderPublicKey = DecryptSenderPublicKey(message.SenderPublicKey, roomKey);
            byte[] plaintext = SecretAeadXChaCha20Poly1305.Decrypt(cipherText, nonce, roomKey, []);
            DecodedRoomMessage payload = DecodePayload(Encoding.UTF8.GetString(plaintext));

            byte[] signaturePayload = BuildSignaturePayload(message.RoomHash, nonce, cipherText, message.SenderPublicKey);
            bool isVerified = Verify(signaturePayload, message.Signature, senderPublicKey);

            return new RenderedMessage
            {
                SentAtUtc = payload.SentAtUtc,
                Sender = payload.SenderName,
                SenderKeyHash = SenderKeyDisplayFormatter.Format(senderPublicKey),
                Body = payload.Text,
                IsVerified = isVerified,
                IsError = false,
                IsPending = false,
                DeliveryFailed = false
            };
        }
        catch (Exception ex)
        {
            return new RenderedMessage
            {
                SentAtUtc = DateTimeOffset.UtcNow,
                Sender = "system",
                SenderKeyHash = string.Empty,
                Body = ex.Message,
                IsVerified = false,
                IsError = true,
                IsPending = false,
                DeliveryFailed = false
            };
        }
    }

    private static DecodedRoomMessage DecodePayload(string json)
    {
        RoomMessageEnvelope? envelope = JsonConvert.DeserializeObject<RoomMessageEnvelope>(json);
        if (envelope?.Content is not null && envelope.Metadata is not null)
        {
            return new DecodedRoomMessage(
                envelope.Metadata.SenderName,
                envelope.Content.Text,
                envelope.Metadata.SentAtUtc);
        }

        LegacyRoomMessagePayload? legacyPayload = JsonConvert.DeserializeObject<LegacyRoomMessagePayload>(json);
        if (legacyPayload is not null)
        {
            return new DecodedRoomMessage(
                legacyPayload.SenderName,
                legacyPayload.Text,
                legacyPayload.SentAtUtc);
        }

        throw new InvalidOperationException("Message payload was null.");
    }

    private static byte[] BuildSignaturePayload(string roomHash, byte[] nonce, byte[] cipherText, string senderPublicKey)
    {
        string canonical = JsonConvert.SerializeObject(new
        {
            roomHash,
            nonce = Convert.ToBase64String(nonce),
            cypherText = Convert.ToBase64String(cipherText),
            senderPublicKey
        });

        return Encoding.UTF8.GetBytes(canonical);
    }

    private static string EncryptSenderPublicKey(string publicKeyJson, byte[] roomKey)
    {
        byte[] nonce = SecretAeadXChaCha20Poly1305.GenerateNonce();
        byte[] plaintext = Encoding.UTF8.GetBytes(publicKeyJson);
        byte[] cipherText = SecretAeadXChaCha20Poly1305.Encrypt(plaintext, nonce, roomKey, []);

        SenderPublicKeyEnvelope envelope = new()
        {
            Nonce = Convert.ToBase64String(nonce),
            CypherText = Convert.ToBase64String(cipherText)
        };

        return JsonConvert.SerializeObject(envelope);
    }

    private static string DecryptSenderPublicKey(string encryptedPublicKeyJson, byte[] roomKey)
    {
        SenderPublicKeyEnvelope envelope = ParseSenderPublicKeyEnvelope(encryptedPublicKeyJson);
        byte[] nonce = Convert.FromBase64String(envelope.Nonce);
        byte[] cipherText = Convert.FromBase64String(envelope.CypherText);
        byte[] plaintext = SecretAeadXChaCha20Poly1305.Decrypt(cipherText, nonce, roomKey, []);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static string Sign(byte[] payload, string privateKeyJson)
    {
        MLDsaPrivateKeyParameters mldsaPrivateKey = (MLDsaPrivateKeyParameters)PrivateKeyFactory.CreateKey(
            DecodeBase64KeyMaterial(privateKeyJson, "private_key"));

        MLDsaSigner mldsaSigner = new(MLDsaParameters.ml_dsa_87, false);
        mldsaSigner.Init(true, mldsaPrivateKey);
        mldsaSigner.BlockUpdate(payload);

        return Convert.ToBase64String(mldsaSigner.GenerateSignature());
    }

    private static bool Verify(byte[] payload, string signatureJson, string publicKeyJson)
    {
        MLDsaPublicKeyParameters mldsaPublicKey = (MLDsaPublicKeyParameters)PublicKeyFactory.CreateKey(
            DecodeBase64KeyMaterial(publicKeyJson, "public_key"));

        MLDsaSigner mldsaSigner = new(MLDsaParameters.ml_dsa_87, false);
        mldsaSigner.Init(false, mldsaPublicKey);
        mldsaSigner.BlockUpdate(payload);

        return mldsaSigner.VerifySignature(DecodeBase64KeyMaterial(signatureJson, "signature"));
    }

    private static byte[] DeriveEncryptionKey(string roomKey)
    {
        byte[] domainSeparatedBytes = Encoding.UTF8.GetBytes($"messaging-2-room-key-v1:{roomKey}");
        return Shake256(domainSeparatedBytes, XChaChaKeyBytes);
    }

    private static byte[] Shake256(byte[] input, int outputLength)
    {
        ShakeDigest digest = new(256);
        digest.BlockUpdate(input, 0, input.Length);
        byte[] output = new byte[outputLength];
        digest.OutputFinal(output, 0, outputLength);
        return output;
    }

    private static SenderPublicKeyEnvelope ParseSenderPublicKeyEnvelope(string json)
    {
        return JsonConvert.DeserializeObject<SenderPublicKeyEnvelope>(json)
            ?? throw new InvalidOperationException("Message sender public key is not a valid encrypted envelope JSON.");
    }

    private static byte[] DecodeBase64KeyMaterial(string value, string fieldName)
    {
        string normalized = NormalizeBase64(value);
        if (normalized.Contains("BASE64_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{fieldName}' still contains placeholder text. Generate a real identity with " +
                "'dotnet run --project src/Tui/Tui.csproj -- generate-identity --name <Name>' and paste the resulting key blobs into config.toml.");
        }

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"'{fieldName}' is not valid Base64 key material. If you pasted generated keys from the terminal, make sure you copied the full Base64 value without truncation.",
                ex);
        }
    }

    private static string NormalizeBase64(string value)
    {
        StringBuilder builder = new(value.Length);

        foreach (char character in value.Where(character => !char.IsWhiteSpace(character)))
        {
            builder.Append(character);
        }

        return builder.ToString();
    }
}

internal sealed class SenderPublicKeyEnvelope
{
    public string Nonce { get; init; } = string.Empty;

    public string CypherText { get; init; } = string.Empty;
}

internal sealed record DecodedRoomMessage
{
    public DecodedRoomMessage(string senderName, string text, DateTimeOffset sentAtUtc)
    {
        SenderName = senderName;
        Text = text;
        SentAtUtc = sentAtUtc;
    }

    public string SenderName { get; init; }

    public string Text { get; init; }

    public DateTimeOffset SentAtUtc { get; init; }
}
