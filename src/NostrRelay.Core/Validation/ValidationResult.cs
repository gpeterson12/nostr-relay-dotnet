namespace NostrRelay.Core.Validation;

/// <summary>
/// Outcome of a single validator or the full pipeline. <see cref="Reason"/> follows the
/// Nostr OK-message convention of a machine-readable prefix (Section 2.2), e.g.
/// "invalid: id mismatch", "blocked: pubkey not allowed", so the server layer can forward
/// it verbatim in the ["OK", id, false, reason] response.
/// </summary>
public readonly record struct ValidationResult
{
    public bool IsValid { get; }

    public string? Reason { get; }

    private ValidationResult(bool isValid, string? reason)
    {
        IsValid = isValid;
        Reason = reason;
    }

    public static ValidationResult Success() => new(true, null);

    public static ValidationResult Failure(string reason) => new(false, reason);
}
