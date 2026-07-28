using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Petnames;

namespace Tui.Services;

internal static class SenderKeyDisplayFormatter
{
    public static string Format(string senderPublicKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes($"messaging-2-sender-key-display-v1:{senderPublicKey}");
        ShakeDigest digest = new(256);
        digest.BlockUpdate(keyBytes, 0, keyBytes.Length);

        byte[] output = new byte[3];
        digest.OutputFinal(output, 0, output.Length);

        return string.Join("-",
            SelectWord(Words.MediumAdjectives, output[0]),
            SelectWord(Words.MediumAdjectives, output[1]),
            SelectWord(Words.MediumNames, output[2]));
    }

    private static string SelectWord(IReadOnlyList<string> words, byte index)
    {
        return words[index % words.Count];
    }
}
