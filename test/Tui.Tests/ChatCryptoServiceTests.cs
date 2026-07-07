using System.Reflection;
using System.Text;
using Core.Dto;
using Newtonsoft.Json.Linq;
using Sodium;
using Tui.Configuration;
using Tui.Services;

namespace Tui.Tests;

[TestClass]
public sealed class ChatCryptoServiceTests
{
    private const string TestMessageText = "hello from the encrypted envelope";
    private static readonly RoomLeafNode s_room = new()
    {
        Name = "Room1",
        Path = "rooms.Group1.Room1",
        Key = "Nah-ru Xela Bhak Cali Squill",
        IdentityName = "IdentityA"
    };

    [TestMethod]
    public void CreateEncryptedMessage_StoresMetadataInsideEnvelope()
    {
        ChatCryptoService service = new();
        IdentityConfig identity = LoadIdentityFromConfig();

        EncryptedMessageDto message = service.CreateEncryptedMessage(s_room, identity, TestMessageText);
        string plaintextJson = DecryptPayloadJson(s_room.Key, message);
        JObject payload = JObject.Parse(plaintextJson);

        Assert.AreEqual(TestMessageText, payload["content"]?["text"]?.Value<string>());
        Assert.AreEqual(identity.Name, payload["metadata"]?["sender_name"]?.Value<string>());
        Assert.IsNotNull(payload["metadata"]?["sent_at_utc"]?.Value<string>());
    }

    [TestMethod]
    public void CreateEncryptedMessage_EncryptsSenderPublicKeyWithRoomKey()
    {
        ChatCryptoService service = new();
        IdentityConfig identity = LoadIdentityFromConfig();

        EncryptedMessageDto message = service.CreateEncryptedMessage(s_room, identity, TestMessageText);
        byte[] roomKeyBytes = InvokePrivateStatic<byte[]>(nameof(ChatCryptoService), "DeriveEncryptionKey", s_room.Key);
        string decryptedSenderPublicKey = InvokePrivateStatic<string>(
            nameof(ChatCryptoService),
            "DecryptSenderPublicKey",
            message.SenderPublicKey,
            roomKeyBytes);

        Assert.AreNotEqual(identity.PublicKey, message.SenderPublicKey);
        Assert.AreEqual(identity.PublicKey, decryptedSenderPublicKey);
    }

    [TestMethod]
    public void TryReadMessage_ReadsLegacyPayloadShape()
    {
        ChatCryptoService service = new();
        IdentityConfig identity = LoadIdentityFromConfig();
        DateTimeOffset expectedSentAtUtc = new(2026, 7, 7, 11, 22, 33, TimeSpan.Zero);
        string legacyPayloadJson = """
            {
              "sender_name": "TheLegend27",
              "text": "legacy payload",
              "sent_at_utc": "2026-07-07T11:22:33+00:00"
            }
            """;

        EncryptedMessageDto legacyMessage = CreateSignedEncryptedMessage(s_room, identity, legacyPayloadJson);
        RenderedMessage renderedMessage = service.TryReadMessage(s_room, legacyMessage);

        Assert.IsFalse(renderedMessage.IsError);
        Assert.AreEqual("TheLegend27", renderedMessage.Sender);
        Assert.AreEqual("legacy payload", renderedMessage.Body);
        Assert.AreEqual(expectedSentAtUtc, renderedMessage.SentAtUtc);
    }

    private static string DecryptPayloadJson(string roomKey, EncryptedMessageDto message)
    {
        byte[] roomKeyBytes = InvokePrivateStatic<byte[]>(nameof(ChatCryptoService), "DeriveEncryptionKey", roomKey);
        byte[] nonce = Convert.FromBase64String(message.Nonce);
        byte[] cipherText = Convert.FromBase64String(message.CypherText);
        byte[] plaintext = SecretAeadXChaCha20Poly1305.Decrypt(cipherText, nonce, roomKeyBytes, []);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static EncryptedMessageDto CreateSignedEncryptedMessage(RoomLeafNode room, IdentityConfig identity, string payloadJson)
    {
        byte[] roomKeyBytes = InvokePrivateStatic<byte[]>(nameof(ChatCryptoService), "DeriveEncryptionKey", room.Key);
        byte[] nonce = SecretAeadXChaCha20Poly1305.GenerateNonce();
        byte[] plaintext = Encoding.UTF8.GetBytes(payloadJson);
        byte[] cipherText = SecretAeadXChaCha20Poly1305.Encrypt(plaintext, nonce, roomKeyBytes, []);

        ChatCryptoService service = new();
        string roomHash = service.ComputeRoomHash(room.Key);
        string encryptedSenderPublicKey = InvokePrivateStatic<string>(
            nameof(ChatCryptoService),
            "EncryptSenderPublicKey",
            identity.PublicKey,
            roomKeyBytes);
        byte[] signaturePayload = InvokePrivateStatic<byte[]>(
            nameof(ChatCryptoService),
            "BuildSignaturePayload",
            roomHash,
            nonce,
            cipherText,
            encryptedSenderPublicKey);
        string signature = InvokePrivateStatic<string>(
            nameof(ChatCryptoService),
            "Sign",
            signaturePayload,
            identity.PrivateKey);

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

    private static IdentityConfig LoadIdentityFromConfig()
    {
        string configPath = Path.Combine(GetSolutionRoot(), "src", "Tui", "config.toml");
        string config = File.ReadAllText(configPath);

        string? name = MatchValue(config, "name");
        string? publicKey = MatchTripleQuotedValue(config, "public_key");
        string? privateKey = MatchTripleQuotedValue(config, "private_key");

        Assert.IsNotNull(name);
        Assert.IsNotNull(publicKey);
        Assert.IsNotNull(privateKey);

        return new IdentityConfig
        {
            Name = name,
            PublicKey = publicKey,
            PrivateKey = privateKey
        };
    }

    private static string GetSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Messaging-2.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the solution root.");
    }

    private static string? MatchValue(string source, string key)
    {
        string pattern = $"^{key}\\s*=\\s*\"(?<value>[^\"]+)\"";
        return System.Text.RegularExpressions.Regex.Match(source, pattern, System.Text.RegularExpressions.RegexOptions.Multiline)
            .Groups["value"]
            .Value;
    }

    private static string? MatchTripleQuotedValue(string source, string key)
    {
        string pattern = $"{key}\\s*=\\s*'''(?<value>.*?)'''";
        return System.Text.RegularExpressions.Regex.Match(source, pattern, System.Text.RegularExpressions.RegexOptions.Singleline)
            .Groups["value"]
            .Value;
    }

    private static T InvokePrivateStatic<T>(string typeName, string methodName, params object[] arguments)
    {
        Type type = typeof(ChatCryptoService).Assembly.GetType($"Tui.Services.{typeName}")
            ?? throw new InvalidOperationException($"Could not resolve type '{typeName}'.");
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Could not resolve method '{methodName}'.");

        object? result = method.Invoke(null, arguments);
        return result is T typedResult
            ? typedResult
            : throw new InvalidOperationException($"Method '{methodName}' did not return {typeof(T).Name}.");
    }
}
