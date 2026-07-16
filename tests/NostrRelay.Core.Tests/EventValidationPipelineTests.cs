using System.Text;
using NBitcoin.Secp256k1;
using NostrRelay.Core.Crypto;
using NostrRelay.Core.Serialization;
using NostrRelay.Core.Validation;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NostrRelay.Core.Tests;

public class EventValidationPipelineTests
{
    private readonly EventValidationPipeline _pipeline = EventValidationPipeline.Default(new Secp256k1SignatureVerifier());

    [Fact]
    public void Validate_Succeeds_ForGenuinelySignedEvent()
    {
        NostrEvent evt = BuildSignedEvent();

        ValidationResult result = _pipeline.Validate(evt);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenIdDoesNotMatchContent()
    {
        NostrEvent evt = BuildSignedEvent() with { Content = "tampered after signing" };

        ValidationResult result = _pipeline.Validate(evt);

        Assert.False(result.IsValid);
        Assert.Contains("id does not match", result.Reason);
    }

    [Fact]
    public void Validate_Fails_WhenSignatureDoesNotMatchPubkey()
    {
        NostrEvent evt = BuildSignedEvent();
        var (otherPubkeyHex, _) = GenerateKeyPairWithPrivateKey("a-different-seed");
        NostrEvent tamperedButIdConsistentEvt = ReplacePubkeyAndRecomputeId(evt, otherPubkeyHex);

        ValidationResult result = _pipeline.Validate(tamperedButIdConsistentEvt);

        Assert.False(result.IsValid);
        Assert.Contains("signature verification failed", result.Reason);
    }

    [Fact]
    public void Validate_Fails_StructurallyBeforeCheckingSignature()
    {
        NostrEvent evt = BuildSignedEvent() with { Kind = -1 };

        ValidationResult result = _pipeline.Validate(evt);

        Assert.False(result.IsValid);
        Assert.Contains("kind must be between 0 and 65535", result.Reason);
    }

    private static NostrEvent BuildSignedEvent()
    {
        (var pubkeyHex, ECPrivKey privateKey) = GenerateKeyPairWithPrivateKey("nostr-relay-pipeline-test-seed");

        const long createdAt = 1700000000;
        const int kind = 1;
        IReadOnlyList<IReadOnlyList<string>> tags = [["e", "abcd"]];
        const string content = "pipeline test event";

        var id = NostrEventCanonicalSerializer.ComputeId(pubkeyHex, createdAt, kind, tags, content);
        var sigHex = Convert.ToHexStringLower(privateKey.SignBIP340(Convert.FromHexString(id)).ToBytes());

        return new NostrEvent
        {
            Id = id,
            Pubkey = pubkeyHex,
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags,
            Content = content,
            Sig = sigHex
        };
    }

    /// <summary>
    /// Swaps in a different pubkey and recomputes the id so structural and id validation
    /// both pass, isolating the signature check as the only thing that should fail.
    /// </summary>
    private static NostrEvent ReplacePubkeyAndRecomputeId(NostrEvent evt, string newPubkeyHex)
    {
        var newId = NostrEventCanonicalSerializer.ComputeId(newPubkeyHex, evt.CreatedAt, evt.Kind, evt.Tags, evt.Content);
        return evt with { Pubkey = newPubkeyHex, Id = newId };
    }

    private static (string PubkeyHex, ECPrivKey PrivKey) GenerateKeyPairWithPrivateKey(string seed)
    {
        var privateKeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var privateKey = ECPrivKey.Create(privateKeyBytes);
        var pubkeyHex = Convert.ToHexStringLower(privateKey.CreateXOnlyPubKey().ToBytes());
        return (pubkeyHex, privateKey);
    }
}