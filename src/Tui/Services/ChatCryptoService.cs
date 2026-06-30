using System.Text;
using Core.Dto;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
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
        RoomMessagePayload payload = new()
        {
            SenderName = identity.Name,
            Text = text,
            SentAtUtc = DateTimeOffset.UtcNow,
        };

        byte[] plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        byte[] cipherText = SecretAeadXChaCha20Poly1305.Encrypt(plaintext, nonce, roomKey, []);
        string roomHash = ComputeRoomHash(room.Key);
        byte[] signaturePayload = BuildSignaturePayload(roomHash, nonce, cipherText, identity.PublicKey);
        string signature = Sign(signaturePayload, identity.PrivateKey);

        return new EncryptedMessageDto
        {
            Id = 0,
            RoomHash = roomHash,
            SenderPublicKey = identity.PublicKey,
            Nonce = Convert.ToBase64String(nonce),
            CypherText = Convert.ToBase64String(cipherText),
            Signature = signature,
        };
    }

    public RenderedMessage TryReadMessage(RoomLeafNode room, EncryptedMessageDto message)
    {
        try
        {
            byte[] roomKey = DeriveEncryptionKey(room.Key);
            byte[] nonce = Convert.FromBase64String(message.Nonce);
            byte[] cipherText = Convert.FromBase64String(message.CypherText);
            byte[] plaintext = SecretAeadXChaCha20Poly1305.Decrypt(cipherText, nonce, roomKey, []);
            RoomMessagePayload? payload = JsonConvert.DeserializeObject<RoomMessagePayload>(Encoding.UTF8.GetString(plaintext));
            if (payload is null)
            {
                throw new InvalidOperationException("Message payload was null.");
            }

            byte[] signaturePayload = BuildSignaturePayload(message.RoomHash, nonce, cipherText, message.SenderPublicKey);
            bool isVerified = Verify(signaturePayload, message.Signature, message.SenderPublicKey);
            string timestamp = payload.SentAtUtc.ToLocalTime().ToString("u");

            return new RenderedMessage
            {
                Timestamp = timestamp,
                Sender = payload.SenderName,
                Body = payload.Text,
                IsVerified = isVerified,
                IsError = false,
            };
        }
        catch (Exception ex)
        {
            return new RenderedMessage
            {
                Timestamp = DateTimeOffset.Now.ToString("u"),
                Sender = "system",
                Body = ex.Message,
                IsVerified = false,
                IsError = true,
            };
        }
    }

    private static byte[] BuildSignaturePayload(string roomHash, byte[] nonce, byte[] cipherText, string senderPublicKey)
    {
        string canonical = JsonConvert.SerializeObject(new
        {
            roomHash,
            nonce = Convert.ToBase64String(nonce),
            cypherText = Convert.ToBase64String(cipherText),
            senderPublicKey,
        });

        return Encoding.UTF8.GetBytes(canonical);
    }

    private static string Sign(byte[] payload, string privateKeyJson)
    {
        CompositePrivateKeyEnvelope envelope = ParsePrivateEnvelope(privateKeyJson);

        MLDsaPrivateKeyParameters mldsaPrivateKey = (MLDsaPrivateKeyParameters)PrivateKeyFactory.CreateKey(
            DecodeBase64KeyMaterial(envelope.Mldsa, "private_key.mldsa"));
        SlhDsaPrivateKeyParameters slhDsaPrivateKey = (SlhDsaPrivateKeyParameters)PrivateKeyFactory.CreateKey(
            DecodeBase64KeyMaterial(envelope.SlhDsa, "private_key.slhdsa"));

        MLDsaSigner mldsaSigner = new(MLDsaParameters.ml_dsa_87, false);
        mldsaSigner.Init(true, mldsaPrivateKey);
        mldsaSigner.BlockUpdate(payload);

        SlhDsaSigner slhDsaSigner = new(SlhDsaParameters.slh_dsa_shake_256s, false);
        slhDsaSigner.Init(true, slhDsaPrivateKey);
        slhDsaSigner.BlockUpdate(payload);

        CompositeSignatureEnvelope signature = new()
        {
            Mldsa = Convert.ToBase64String(mldsaSigner.GenerateSignature()),
            SlhDsa = Convert.ToBase64String(slhDsaSigner.GenerateSignature()),
        };

        return JsonConvert.SerializeObject(signature);
    }

    private static bool Verify(byte[] payload, string signatureJson, string publicKeyJson)
    {
        CompositeSignatureEnvelope signature = ParseSignatureEnvelope(signatureJson);
        CompositePublicKeyEnvelope publicKeys = ParsePublicEnvelope(publicKeyJson);

        MLDsaPublicKeyParameters mldsaPublicKey = (MLDsaPublicKeyParameters)PublicKeyFactory.CreateKey(
            DecodeBase64KeyMaterial(publicKeys.Mldsa, "public_key.mldsa"));
        SlhDsaPublicKeyParameters slhDsaPublicKey = (SlhDsaPublicKeyParameters)PublicKeyFactory.CreateKey(
            DecodeBase64KeyMaterial(publicKeys.SlhDsa, "public_key.slhdsa"));

        MLDsaSigner mldsaSigner = new(MLDsaParameters.ml_dsa_87, false);
        mldsaSigner.Init(false, mldsaPublicKey);
        mldsaSigner.BlockUpdate(payload);

        SlhDsaSigner slhDsaSigner = new(SlhDsaParameters.slh_dsa_shake_256s, false);
        slhDsaSigner.Init(false, slhDsaPublicKey);
        slhDsaSigner.BlockUpdate(payload);

        return mldsaSigner.VerifySignature(DecodeBase64KeyMaterial(signature.Mldsa, "signature.mldsa"))
            && slhDsaSigner.VerifySignature(DecodeBase64KeyMaterial(signature.SlhDsa, "signature.slhdsa"));
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

    private static CompositePublicKeyEnvelope ParsePublicEnvelope(string json)
    {
        return JsonConvert.DeserializeObject<CompositePublicKeyEnvelope>(json)
            ?? throw new InvalidOperationException("Identity public_key is not valid composite key JSON.");
    }

    private static CompositePrivateKeyEnvelope ParsePrivateEnvelope(string json)
    {
        return JsonConvert.DeserializeObject<CompositePrivateKeyEnvelope>(json)
            ?? throw new InvalidOperationException("Identity private_key is not valid composite key JSON.");
    }

    private static CompositeSignatureEnvelope ParseSignatureEnvelope(string json)
    {
        return JsonConvert.DeserializeObject<CompositeSignatureEnvelope>(json)
            ?? throw new InvalidOperationException("Message signature is not valid composite signature JSON.");
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
                $"'{fieldName}' is not valid Base64 key material. If you pasted generated keys from the terminal, make sure you copied the full JSON string without truncation.",
                ex);
        }
    }

    private static string NormalizeBase64(string value)
    {
        StringBuilder builder = new(value.Length);

        foreach (char character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

internal sealed class RoomMessagePayload
{
    [JsonProperty("sender_name")]
    public required string SenderName { get; init; }

    [JsonProperty("text")]
    public required string Text { get; init; }

    [JsonProperty("sent_at_utc")]
    public required DateTimeOffset SentAtUtc { get; init; }
}
