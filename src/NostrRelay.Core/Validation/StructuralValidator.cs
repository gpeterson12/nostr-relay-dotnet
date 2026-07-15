namespace NostrRelay.Core.Validation;

/// <summary>
/// First pipeline step (Section 2.3 rule 1): checks shape and obviously-invalid values
/// before any cryptography runs. Deliberately cheap, this exists to reject garbage fast
/// without paying for a hash or a signature check.
///
/// Timestamp sanity beyond "not negative" is a policy concern (rule 4, configurable
/// window) and is intentionally not enforced here.
/// </summary>
public sealed class StructuralValidator : IEventValidator
{
    private const int HexIdLength = 64;
    private const int HexPubkeyLength = 64;
    private const int HexSigLength = 128;

    public ValidationResult Validate(NostrEvent evt)
    {
        if (evt.Id.Length != HexIdLength || !IsLowercaseHex(evt.Id))
            return ValidationResult.Failure("invalid: id must be 64 lowercase hex characters");

        if (evt.Pubkey.Length != HexPubkeyLength || !IsLowercaseHex(evt.Pubkey))
            return ValidationResult.Failure("invalid: pubkey must be 64 lowercase hex characters");

        if (evt.Sig.Length != HexSigLength || !IsLowercaseHex(evt.Sig))
            return ValidationResult.Failure("invalid: sig must be 128 lowercase hex characters");

        if (evt.Kind is < 0 or > 65535)
            return ValidationResult.Failure("invalid: kind must be between 0 and 65535");

        if (evt.CreatedAt <= 0)
            return ValidationResult.Failure("invalid: created_at must be a positive unix timestamp");

        foreach (var tag in evt.Tags)
        {
            if (tag.Count == 0)
                return ValidationResult.Failure("invalid: tag entries must not be empty");
        }

        return ValidationResult.Success();
    }

    private static bool IsLowercaseHex(string s)
    {
        foreach (var c in s)
        {
            var isLowercaseHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowercaseHex)
                return false;
        }

        return true;
    }
}