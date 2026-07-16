using System.Text;
using System.Text.Json;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Core.Protocol;
using NostrRelay.Core.Serialization;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NostrRelay.Server.IntegrationTests.TestSupport;

/// <summary>
/// Genuinely signs test events via NBitcoin.Secp256k1, mirroring the self-contained
/// approach used throughout NostrRelay.Core.Tests: no externally sourced test vectors,
/// correctness comes from round-tripping through the same primitives the server's own
/// SignatureValidator wraps.
///
/// Key generation and signing are separate methods (not one combined call) specifically
/// so a test can generate one keypair and sign multiple events with it, needed for any
/// scenario involving the same author publishing more than once (replaceable/addressable
/// supersession, CLOSE behavior, etc.).
/// </summary>
public static class NostrTestEvents
{
    public static (ECPrivKey PrivKey, string PubkeyHex) GenerateKeyPair(string seed = "test-seed")
    {
        var privkeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + Guid.NewGuid()));
        var privkey = ECPrivKey.Create(privkeyBytes);
        var pubkeyHex = Convert.ToHexStringLower(privkey.CreateXOnlyPubKey().ToBytes());
        return (privkey, pubkeyHex);
    }

    public static NostrEvent SignWithKey(
        ECPrivKey privkey,
        string pubkeyHex,
        string content,
        int kind = 1,
        long createdAt = 1700000000,
        IReadOnlyList<IReadOnlyList<string>>? tags = null)
    {
        tags ??= [];
        var id = NostrEventCanonicalSerializer.ComputeId(pubkeyHex, createdAt, kind, tags, content);
        var sigHex = Convert.ToHexStringLower(privkey.SignBIP340(Convert.FromHexString(id)).ToBytes());

        return new NostrEvent
        {
            Id = id,
            Pubkey = pubkeyHex,
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags,
            Content = content,
            Sig = sigHex,
        };
    }

    /// <summary>Convenience for the common case of a single event from a fresh, throwaway
    /// keypair. For scenarios needing the same author to publish more than once, call
    /// <see cref="GenerateKeyPair"/> once and <see cref="SignWithKey"/> per event instead.</summary>
    public static (NostrEvent Event, string PubkeyHex) SignEvent(
        string content,
        int kind = 1,
        string seed = "test-seed",
        long createdAt = 1700000000,
        IReadOnlyList<IReadOnlyList<string>>? tags = null)
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair(seed);
        return (SignWithKey(privkey, pubkeyHex, content, kind, createdAt, tags), pubkeyHex);
    }

    public static string ToEventJson(NostrEvent evt) => JsonSerializer.Serialize(evt, NostrJsonOptions.Default);
}
