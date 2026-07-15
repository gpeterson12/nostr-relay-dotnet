using System.Text;
using NBitcoin.Secp256k1;
using NostrRelay.Core.Crypto;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NostrRelay.Core.Tests;

public class Secp256k1SignatureVerifierTests
{
    private readonly Secp256k1SignatureVerifier _verifier = new();

    [Fact]
    public void Verify_ReturnsTrue_ForGenuineSignature()
    {
        var (pubkeyHex, messageHex, sigHex) = SignFixedMessage("hello nostr");

        Assert.True(_verifier.Verify(pubkeyHex, messageHex, sigHex));
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenMessageDoesNotMatchSignature()
    {
        var (pubkeyHex, _, sigHex) = SignFixedMessage("hello nostr");
        var wrongMessageHex = Convert.ToHexStringLower(SHA256.HashData("a different message"u8.ToArray()));

        Assert.False(_verifier.Verify(pubkeyHex, wrongMessageHex, sigHex));
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenSignatureIsTampered()
    {
        var (pubkeyHex, messageHex, sigHex) = SignFixedMessage("hello nostr");
        var tamperedSigBytes = Convert.FromHexString(sigHex);
        tamperedSigBytes[0] ^= 0xFF;
        var tamperedSigHex = Convert.ToHexStringLower(tamperedSigBytes);

        Assert.False(_verifier.Verify(pubkeyHex, messageHex, tamperedSigHex));
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenPubkeyDoesNotMatch()
    {
        var (_, messageHex, sigHex) = SignFixedMessage("hello nostr");
        var (otherPubkeyHex, _, _) = SignFixedMessage("a different key's message", seed: "other-seed");

        Assert.False(_verifier.Verify(otherPubkeyHex, messageHex, sigHex));
    }

    [Theory]
    [InlineData("", "aa", "bb")]
    [InlineData("not-hex-at-all-not-hex-at-all-not-hex-at-all-not-hex-at-all-aa", "bb", "cc")]
    public void Verify_ReturnsFalse_ForMalformedInputs_RatherThanThrowing(
        string pubkeyHex, string messageHex, string sigHex)
    {
        Assert.False(_verifier.Verify(pubkeyHex, messageHex, sigHex));
    }

    /// <summary>
    /// Deterministically derives a private key from <paramref name="seed"/>, signs the
    /// SHA256 hash of <paramref name="message"/>, and returns (pubkeyHex, messageHashHex, sigHex).
    /// Self-contained: no externally sourced test vectors, correctness is checked by
    /// round-tripping through the same NBitcoin.Secp256k1 primitives the verifier wraps.
    /// </summary>
    private static (string PubkeyHex, string MessageHex, string SigHex) SignFixedMessage(
        string message, string seed = "nostr-relay-test-seed")
    {
        var privateKeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var privateKey = ECPrivKey.Create(privateKeyBytes);
        ECXOnlyPubKey pubkey = privateKey.CreateXOnlyPubKey();

        var messageHash = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        SecpSchnorrSignature signature = privateKey.SignBIP340(messageHash);

        var pubkeyBytes = pubkey.ToBytes();
        return (
            Convert.ToHexStringLower(pubkeyBytes),
            Convert.ToHexStringLower(messageHash),
            Convert.ToHexStringLower(signature.ToBytes()));
    }
}
