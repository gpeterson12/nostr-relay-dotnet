using NBitcoin.Secp256k1;

namespace NostrRelay.Core.Crypto;

/// <summary>
/// BIP-340 Schnorr verification backed by NBitcoin.Secp256k1. This is the cryptographic
/// core of the whole relay (Section 2.3): every stored event's authenticity rests on this
/// check, so failures here must be strict (malformed hex, wrong lengths, or an invalid
/// curve point all resolve to "not verified" rather than throwing).
/// </summary>
public sealed class Secp256k1SignatureVerifier : ISignatureVerifier
{
    public bool Verify(string pubkeyHex, string messageHashHex, string signatureHex)
    {
        if (!TryFromHex(pubkeyHex, 32, out var pubkeyBytes) ||
            !TryFromHex(messageHashHex, 32, out var messageBytes) ||
            !TryFromHex(signatureHex, 64, out var signatureBytes))
        {
            return false;
        }

        if (!ECXOnlyPubKey.TryCreate(pubkeyBytes, out ECXOnlyPubKey? pubkey))
            return false;

        if (!SecpSchnorrSignature.TryCreate(signatureBytes, out SecpSchnorrSignature? signature))
            return false;

        return pubkey.SigVerifyBIP340(signature, messageBytes);
    }

    private static bool TryFromHex(string hex, int expectedByteLength, out byte[] bytes)
    {
        if (hex.Length != expectedByteLength * 2 || !IsAllHex(hex))
        {
            bytes = [];
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static bool IsAllHex(string s)
    {
        foreach (var c in s)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
                return false;
        }

        return true;
    }
}
