using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Tomlyn;
using Tomlyn.Model;

namespace Tui.Services;

internal sealed class AppConfigIoService
{
    private const string Header = "MESSAGING2-CONFIG-ENC-V1";
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int KeyBytes = 32;
    private const int TagBytes = 16;
    private const int DuressPasswordChecksumBytes = 32;
    private const int GeneratedDuressPasswordBytes = 32;
    private const int IterationCount = 600_000;

    private readonly PasswordService _passwordService;
    private readonly string _configPath;

    public AppConfigIoService(string configPath, PasswordService passwordService)
    {
        _configPath = configPath;
        _passwordService = passwordService;
    }

    public string LoadConfig()
    {
        string persistedContent = File.ReadAllText(_configPath);
        if (IsEncrypted(persistedContent))
        {
            return Decrypt(persistedContent, _passwordService.GetPassword("Config password: "));
        }

        string password = _passwordService.GetConfirmedPassword(
            "Choose a config password: ",
            "Confirm config password: ");
        string duressPassword = _passwordService.GetConfirmedPassword(
            "Choose a duress password: ",
            "Confirm duress password: ");
        string encryptedContent = Encrypt(persistedContent, password, duressPassword);
        File.WriteAllText(_configPath, encryptedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return persistedContent;
    }

    public async Task<string> ReadAsync(string password, CancellationToken ct = default)
    {
        string persistedContent = await File.ReadAllTextAsync(_configPath, ct);
        return Decrypt(persistedContent, password);
    }

    public async Task WriteAsync(string content, string password, CancellationToken ct = default)
    {
        string? duressPasswordChecksum = null;
        if (File.Exists(_configPath))
        {
            string persistedContent = await File.ReadAllTextAsync(_configPath, ct);
            duressPasswordChecksum = TryReadDuressPasswordChecksum(persistedContent);
        }

        string encryptedContent = Encrypt(content, password, duressPasswordChecksum: duressPasswordChecksum);
        await File.WriteAllTextAsync(_configPath, encryptedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }

    public async Task WriteAsync(string content, string password, string duressPassword, CancellationToken ct = default)
    {
        string encryptedContent = Encrypt(content, password, duressPassword);
        await File.WriteAllTextAsync(_configPath, encryptedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }

    public async Task AddIdentityAsync(GeneratedIdentity identity, CancellationToken ct = default)
    {
        string persistedContent = await File.ReadAllTextAsync(_configPath, ct);
        bool isEncrypted = IsEncrypted(persistedContent);
        string? password = null;
        string content = persistedContent;

        if (isEncrypted)
        {
            password = _passwordService.GetPassword("Config password: ");
            content = Decrypt(persistedContent, password);
        }

        string updatedContent = AppendIdentity(content, identity);

        if (isEncrypted)
        {
            await WriteAsync(updatedContent, password!, ct);
            return;
        }

        await File.WriteAllTextAsync(_configPath, updatedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }

    private static bool IsEncrypted(string content)
    {
        return content.StartsWith(Header, StringComparison.Ordinal);
    }

    private static string Encrypt(
        string plaintext,
        string password,
        string? duressPassword = null,
        string? duressPasswordChecksum = null)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password cannot be empty.");
        }

        if (!string.IsNullOrWhiteSpace(duressPassword))
        {
            if (string.Equals(password, duressPassword, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Duress password must be different from the config password.");
            }

            duressPasswordChecksum = ComputeDuressPasswordChecksum(duressPassword);
        }

        duressPasswordChecksum ??= GenerateDuressPasswordChecksum();

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
            DuressPasswordChecksum = duressPasswordChecksum
        };

        return $"{Header}{Environment.NewLine}{JsonConvert.SerializeObject(envelope, Formatting.Indented)}";
    }

    private string Decrypt(string persistedContent, string password)
    {
        if (!IsEncrypted(persistedContent))
        {
            return persistedContent;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Password cannot be empty.");
        }

        string envelopeJson = persistedContent[Header.Length..].Trim();
        ConfigEncryptionEnvelope envelope = JsonConvert.DeserializeObject<ConfigEncryptionEnvelope>(envelopeJson)
            ?? throw new InvalidOperationException("Encrypted config payload is malformed.");

        if (IsDuressPassword(password, envelope.DuressPasswordChecksum))
        {
            File.Delete(_configPath);
            throw new InvalidOperationException("Duress password accepted; config.toml was deleted.");
        }

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

    private static string? TryReadDuressPasswordChecksum(string persistedContent)
    {
        if (!IsEncrypted(persistedContent))
        {
            return null;
        }

        string envelopeJson = persistedContent[Header.Length..].Trim();
        ConfigEncryptionEnvelope? envelope = JsonConvert.DeserializeObject<ConfigEncryptionEnvelope>(envelopeJson);
        return envelope?.DuressPasswordChecksum;
    }

    private static bool IsDuressPassword(string password, string? duressPasswordChecksum)
    {
        if (string.IsNullOrWhiteSpace(duressPasswordChecksum))
        {
            return false;
        }

        byte[] expectedChecksum = Convert.FromBase64String(duressPasswordChecksum);
        if (expectedChecksum.Length != DuressPasswordChecksumBytes)
        {
            return false;
        }

        byte[] actualChecksum = ComputeDuressPasswordChecksumBytes(password);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualChecksum);
        }
    }

    private static string ComputeDuressPasswordChecksum(string password)
    {
        byte[] checksum = ComputeDuressPasswordChecksumBytes(password);
        try
        {
            return Convert.ToBase64String(checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(checksum);
        }
    }

    private static string GenerateDuressPasswordChecksum()
    {
        byte[] passwordBytes = RandomNumberGenerator.GetBytes(GeneratedDuressPasswordBytes);
        byte[] checksum = Shake256.HashData(passwordBytes, DuressPasswordChecksumBytes);
        try
        {
            return Convert.ToBase64String(checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(checksum);
        }
    }

    private static byte[] ComputeDuressPasswordChecksumBytes(string password)
    {
        return Shake256.HashData(Encoding.UTF8.GetBytes(password), DuressPasswordChecksumBytes);
    }

    private static string AppendIdentity(string content, GeneratedIdentity identity)
    {
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(content)
            ?? throw new InvalidOperationException("Could not parse config.toml.");

        if (!root.TryGetValue("identities", out object identitiesValue) || identitiesValue is not TomlTable identitiesTable)
        {
            throw new InvalidOperationException("config.toml must contain an [identities] section.");
        }

        if (identitiesTable.ContainsKey(identity.Name))
        {
            throw new InvalidOperationException($"Identity '{identity.Name}' already exists in config.toml.");
        }

        string snippet =
            $"[identities.{identity.Name}]{Environment.NewLine}" +
            $"name = \"{identity.Name}\"{Environment.NewLine}" +
            $"public_key = '''{identity.PublicKey}'''{Environment.NewLine}" +
            $"private_key = '''{identity.PrivateKey}'''";

        string trimmedContent = content.TrimEnd();
        return $"{trimmedContent}{Environment.NewLine}{Environment.NewLine}{snippet}{Environment.NewLine}";
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

    [JsonProperty("duress_password_checksum")]
    public string? DuressPasswordChecksum { get; init; }
}
