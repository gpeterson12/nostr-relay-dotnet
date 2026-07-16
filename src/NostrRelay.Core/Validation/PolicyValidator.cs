namespace NostrRelay.Core.Validation;

/// <summary>
/// Fourth pipeline step (Section 2.3 rule 4): pubkey allowlist/blocklist and kind
/// blocklist checks (Section 4.3: "Optional pubkey allowlist/blocklist and kind
/// blocklist, config-driven"). Runs after signature verification: rejecting a
/// well-formed, validly-signed event on policy grounds is a more expensive thing to
/// justify getting wrong than rejecting a malformed one, so the cheaper structural/id/
/// signature checks get first refusal per the pipeline's stated ordering philosophy.
///
/// Takes plain collections rather than any hosting-framework configuration type
/// (<c>Microsoft.Extensions.Options</c>, etc.): <c>NostrRelay.Core</c> has no dependency
/// on ASP.NET Core or the Generic Host, and this keeps it that way. The server layer
/// reads its own configuration and passes the resulting values in when composing the
/// production pipeline.
/// </summary>
public sealed class PolicyValidator(
    IReadOnlyCollection<string> pubkeyAllowlist,
    IReadOnlyCollection<string> pubkeyBlocklist,
    IReadOnlyCollection<int> kindBlocklist) : IEventValidator
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

        return ValidationResult.Success();
    }
}
