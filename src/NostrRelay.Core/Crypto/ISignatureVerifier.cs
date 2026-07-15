namespace NostrRelay.Core.Crypto;

/// <summary>
/// Wraps BIP-340 Schnorr signature verification. Isolated behind an interface so
/// <see cref="Validation.SignatureValidator"/> can be unit tested with a fake verifier,
/// and so the underlying crypto library can be swapped without touching validation logic.
/// </summary>
public interface ISignatureVerifier
{
    /// <summary>
    /// Verifies a BIP-340 Schnorr signature over a 32-byte message hash.
    /// </summary>
    /// <param name="pubkeyHex">32-byte x-only public key, lowercase hex, 64 chars.</param>
    /// <param name="messageHashHex">32-byte message (the event id), lowercase hex, 64 chars.</param>
    /// <param name="signatureHex">64-byte Schnorr signature, lowercase hex, 128 chars.</param>
    bool Verify(string pubkeyHex, string messageHashHex, string signatureHex);
}
