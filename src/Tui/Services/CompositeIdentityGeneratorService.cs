using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Tui.Services;

internal sealed class CompositeIdentityGeneratorService
{
    public CompositeIdentity Generate(string name)
    {
        SecureRandom secureRandom = new();

        MLDsaKeyPairGenerator mldsaGenerator = new();
        mldsaGenerator.Init(new MLDsaKeyGenerationParameters(secureRandom, MLDsaParameters.ml_dsa_87));
        AsymmetricCipherKeyPair mldsaKeyPair = mldsaGenerator.GenerateKeyPair();

        SlhDsaKeyPairGenerator slhDsaGenerator = new();
        slhDsaGenerator.Init(new SlhDsaKeyGenerationParameters(secureRandom, SlhDsaParameters.slh_dsa_shake_256s));
        AsymmetricCipherKeyPair slhDsaKeyPair = slhDsaGenerator.GenerateKeyPair();

        CompositePublicKeyEnvelope publicEnvelope = new()
        {
            Mldsa = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(mldsaKeyPair.Public).GetEncoded()),
            SlhDsa = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(slhDsaKeyPair.Public).GetEncoded())
        };

        CompositePrivateKeyEnvelope privateEnvelope = new()
        {
            Mldsa = Convert.ToBase64String(PrivateKeyInfoFactory.CreatePrivateKeyInfo(mldsaKeyPair.Private).GetEncoded()),
            SlhDsa = Convert.ToBase64String(PrivateKeyInfoFactory.CreatePrivateKeyInfo(slhDsaKeyPair.Private).GetEncoded())
        };

        return new CompositeIdentity
        {
            Name = name,
            PublicKeyJson = JsonConvert.SerializeObject(publicEnvelope),
            PrivateKeyJson = JsonConvert.SerializeObject(privateEnvelope)
        };
    }
}

internal sealed class CompositeIdentity
{
    public required string Name { get; init; }
    public required string PublicKeyJson { get; init; }
    public required string PrivateKeyJson { get; init; }
}

internal sealed class CompositePublicKeyEnvelope
{
    [JsonProperty("mldsa")]
    public required string Mldsa { get; init; }

    [JsonProperty("slhdsa")]
    public required string SlhDsa { get; init; }
}

internal sealed class CompositePrivateKeyEnvelope
{
    [JsonProperty("mldsa")]
    public required string Mldsa { get; init; }

    [JsonProperty("slhdsa")]
    public required string SlhDsa { get; init; }
}

internal sealed class CompositeSignatureEnvelope
{
    [JsonProperty("mldsa")]
    public required string Mldsa { get; init; }

    [JsonProperty("slhdsa")]
    public required string SlhDsa { get; init; }
}
