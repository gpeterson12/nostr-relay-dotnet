using NostrRelay.Core.Crypto;

namespace NostrRelay.Core.Validation;

/// <summary>
/// Third pipeline step (Section 2.3 rule 3): the cryptographic core of the whole system.
/// Must run after <see cref="IdValidator"/>, since a valid signature over a mismatched id
/// proves nothing, the id itself has to already be known-correct for this check to mean
/// anything.
/// </summary>
public sealed class SignatureValidator(ISignatureVerifier verifier) : IEventValidator
{
    public ValidationResult Validate(NostrEvent evt)
    {
        var isValid = verifier.Verify(evt.Pubkey, evt.Id, evt.Sig);

        return isValid
            ? ValidationResult.Success()
            : ValidationResult.Failure("invalid: signature verification failed");
    }
}
