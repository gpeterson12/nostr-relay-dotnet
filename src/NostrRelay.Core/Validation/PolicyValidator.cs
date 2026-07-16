namespace NostrRelay.Core.Validation;

/// <summary>
/// Fourth pipeline step (Section 2.3 rule 4): pubkey allowlist/blocklist, kind blocklist,
/// and timestamp sanity checks. Runs after signature verification: rejecting a
/// well-formed, validly-signed event on policy grounds is a more expensive thing to
/// justify getting wrong than rejecting a malformed one, so the cheaper structural/id/
/// signature checks get first refusal per the pipeline's stated ordering philosophy.
///
/// The <c>createdAtLowerLimitSeconds</c>/<c>createdAtUpperLimitSeconds</c> parameters are
/// window sizes in seconds relative to "now" at validation time, not absolute Unix
/// timestamps, matching NIP-11's own <c>created_at_lower_limit</c>/<c>upper_limit</c>
/// convention (confirmed against a real relay's published values: nostr.wine advertises
/// 94608000 (~3 years) and 300 (5 minutes), values far too small to be epoch timestamps
/// themselves). Guards two different things: a lower limit rejects backdated spam;
/// an upper limit rejects claims of a wildly future timestamp, which matters
/// specifically for replaceable events, where a bogus future created_at could keep a
/// forged "latest version" permanently un-supersedable by the real owner's future updates.
///
/// Takes plain collections/values rather than any hosting-framework configuration type
/// (<c>Microsoft.Extensions.Options</c>, etc.): <c>NostrRelay.Core</c> has no dependency
/// on ASP.NET Core or the Generic Host, and this keeps it that way. The server layer
/// reads its own configuration and passes the resulting values in when composing the
/// production pipeline.
/// </summary>
public sealed class PolicyValidator(
    IReadOnlyCollection<string> pubkeyAllowlist,
    IReadOnlyCollection<string> pubkeyBlocklist,
    IReadOnlyCollection<int> kindBlocklist,
    long createdAtLowerLimitSeconds,
    long createdAtUpperLimitSeconds) : IEventValidator
{
    public ValidationResult Validate(NostrEvent evt)
    {
        // An allowlist, when non-empty, is exclusive: only listed pubkeys may publish.
        // An empty allowlist means "no allowlist configured" (allow everyone), not
        // "allow nobody" — an empty list is the "unset" state, not a maximally strict one.
        if (pubkeyAllowlist.Count > 0 && !pubkeyAllowlist.Contains(evt.Pubkey))
            return ValidationResult.Failure("blocked: pubkey is not on the allowlist");

        if (pubkeyBlocklist.Contains(evt.Pubkey))
            return ValidationResult.Failure("blocked: pubkey is blocklisted");

        if (kindBlocklist.Contains(evt.Kind))
            return ValidationResult.Failure("blocked: this event kind is not accepted by this relay");

        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (evt.CreatedAt < nowSeconds - createdAtLowerLimitSeconds)
            return ValidationResult.Failure("invalid: created_at is too far in the past");

        if (evt.CreatedAt > nowSeconds + createdAtUpperLimitSeconds)
            return ValidationResult.Failure("invalid: created_at is too far in the future");

        return ValidationResult.Success();
    }
}