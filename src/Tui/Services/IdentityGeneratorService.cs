using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Tui.Services;

internal sealed class IdentityGeneratorService
{
    public GeneratedIdentity Generate(string name)
    {
        SecureRandom secureRandom = new();

        MLDsaKeyPairGenerator keyPairGenerator = new();
        keyPairGenerator.Init(new MLDsaKeyGenerationParameters(secureRandom, MLDsaParameters.ml_dsa_87));
        AsymmetricCipherKeyPair keyPair = keyPairGenerator.GenerateKeyPair();

        return new GeneratedIdentity
        {
            Name = name,
            PublicKey = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(keyPair.Public).GetEncoded()),
            PrivateKey = Convert.ToBase64String(PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private).GetEncoded())
        };
    }
}

internal sealed class GeneratedIdentity
{
    public required string Name { get; init; }
    public required string PublicKey { get; init; }
    public required string PrivateKey { get; init; }
}
