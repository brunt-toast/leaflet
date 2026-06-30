using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Tui.Configuration;

internal interface IConfigProtectionService
{
    bool IsEncrypted(string content);
    string Encrypt(string plaintext, string password);
    string Decrypt(string encryptedContent, string password);
}

internal sealed class ConfigProtectionService : IConfigProtectionService
{
    private const string Header = "MESSAGING2-CONFIG-ENC-V1";
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int KeyBytes = 32;
    private const int TagBytes = 16;
    private const int IterationCount = 600_000;

    public bool IsEncrypted(string content)
    {
        return content.StartsWith(Header, StringComparison.Ordinal);
    }

    public string Encrypt(string plaintext, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password cannot be empty.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipherText = new byte[plaintextBytes.Length];
        byte[] tag = new byte[TagBytes];
        byte[] key = DeriveKey(password, salt);

        try
        {
            using AesGcm aesGcm = new(key, TagBytes);
            aesGcm.Encrypt(nonce, plaintextBytes, cipherText, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        ConfigEncryptionEnvelope envelope = new()
        {
            Kdf = "PBKDF2-SHA256",
            Iterations = IterationCount,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            CipherText = Convert.ToBase64String(cipherText),
            Tag = Convert.ToBase64String(tag),
        };

        return $"{Header}{Environment.NewLine}{JsonConvert.SerializeObject(envelope, Formatting.Indented)}";
    }

    public string Decrypt(string encryptedContent, string password)
    {
        if (!IsEncrypted(encryptedContent))
        {
            return encryptedContent;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password cannot be empty.");
        }

        string envelopeJson = encryptedContent[Header.Length..].Trim();
        ConfigEncryptionEnvelope envelope = JsonConvert.DeserializeObject<ConfigEncryptionEnvelope>(envelopeJson)
            ?? throw new InvalidOperationException("Encrypted config payload is malformed.");

        byte[] salt = Convert.FromBase64String(envelope.Salt);
        byte[] nonce = Convert.FromBase64String(envelope.Nonce);
        byte[] cipherText = Convert.FromBase64String(envelope.CipherText);
        byte[] tag = Convert.FromBase64String(envelope.Tag);
        byte[] plaintext = new byte[cipherText.Length];
        byte[] key = DeriveKey(password, salt, envelope.Iterations);

        try
        {
            using AesGcm aesGcm = new(key, TagBytes);
            aesGcm.Decrypt(nonce, cipherText, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Incorrect password or corrupted encrypted config.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations = IterationCount)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
    }
}

internal sealed class ConfigEncryptionEnvelope
{
    [JsonProperty("kdf")]
    public required string Kdf { get; init; }

    [JsonProperty("iterations")]
    public required int Iterations { get; init; }

    [JsonProperty("salt")]
    public required string Salt { get; init; }

    [JsonProperty("nonce")]
    public required string Nonce { get; init; }

    [JsonProperty("cipher_text")]
    public required string CipherText { get; init; }

    [JsonProperty("tag")]
    public required string Tag { get; init; }
}
