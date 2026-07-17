using System.Text;
using BenchmarkDotNet.Attributes;
using NBitcoin.Secp256k1;
using NostrRelay.Core.Crypto;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NostrRelay.Benchmarks;

/// <summary>
/// Section 4.1: "Signature verification throughput: benchmark raw Schnorr
/// verifications/sec on target hardware (informational, not user-facing latency, but a
/// good BenchmarkDotNet number for the README)."
///
/// One fixed valid (pubkey, message, signature) triple, genuinely signed at setup time
/// (same self-contained approach used throughout the test suite: no external test
/// vectors, correctness comes from round-tripping through the same NBitcoin.Secp256k1
/// primitives Secp256k1SignatureVerifier itself wraps). BenchmarkDotNet handles iteration
/// counts and statistics; this only needs to supply one call per invocation.
/// </summary>
[MemoryDiagnoser]
public class SignatureVerificationBenchmarks
{
    private Secp256k1SignatureVerifier _verifier = null!;
    private string _pubkeyHex = null!;
    private string _messageHex = null!;
    private string _sigHex = null!;

    [GlobalSetup]
    public void Setup()
    {
        _verifier = new Secp256k1SignatureVerifier();

        var privkeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes("benchmark-signing-key"));
        var privkey = ECPrivKey.Create(privkeyBytes);
        ECXOnlyPubKey pubkey = privkey.CreateXOnlyPubKey();
        _pubkeyHex = Convert.ToHexStringLower(pubkey.ToBytes());

        var messageBytes = SHA256.HashData(Encoding.UTF8.GetBytes("benchmark message content"));
        _messageHex = Convert.ToHexStringLower(messageBytes);

        SecpSchnorrSignature signature = privkey.SignBIP340(messageBytes);
        _sigHex = Convert.ToHexStringLower(signature.ToBytes());
    }

    [Benchmark]
    public bool VerifyValidSignature() => _verifier.Verify(_pubkeyHex, _messageHex, _sigHex);
}
