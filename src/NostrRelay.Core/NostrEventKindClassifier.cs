namespace NostrRelay.Core;

/// <summary>
/// The four storage/query semantics NIP-01 defines per kind range (Section 3.3).
/// </summary>
public enum NostrEventKindCategory
{
    /// <summary>All versions kept, queryable by id/time/etc.</summary>
    Regular,

    /// <summary>Only the latest event per (pubkey, kind) is retained.</summary>
    Replaceable,

    /// <summary>Never stored; broadcast live to matching subscriptions, then discarded.</summary>
    Ephemeral,

    /// <summary>Like replaceable, but keyed on (pubkey, kind, d-tag-value) instead of just (pubkey, kind).</summary>
    Addressable
}

/// <summary>
/// Classifies a kind into its NIP-01 storage category, per the current spec's kind-range
/// conventions:
/// <list type="bullet">
/// <item>Replaceable: kind 0, kind 3, or 10000 &lt;= kind &lt; 20000.</item>
/// <item>Ephemeral: 20000 &lt;= kind &lt; 30000.</item>
/// <item>Addressable: 30000 &lt;= kind &lt; 40000.</item>
/// <item>Regular: everything else, including the explicitly-regular ranges
/// (1000 &lt;= kind &lt; 10000, 4 &lt;= kind &lt; 45, kind 1, kind 2).</item>
/// </list>
///
/// NIP-01 itself only explicitly names ranges for "regular" (1000&lt;=n&lt;10000 ||
/// 4&lt;=n&lt;45 || n==1 || n==2) alongside the replaceable/ephemeral/addressable ranges;
/// kinds outside all of these (e.g. 46-999, 40000-65535) aren't explicitly addressed by
/// the spec text. This classifier treats any kind not matched by a more specific
/// category as Regular, matching the convention most real-world relay implementations
/// converged on ("these are just conventions and relay implementations may differ" per
/// the spec itself).
/// </summary>
public static class NostrEventKindClassifier
{
    public static NostrEventKindCategory Classify(int kind)
    {
        if (kind == 0 || kind == 3 || (kind is >= 10000 and < 20000))
            return NostrEventKindCategory.Replaceable;

        if (kind is >= 20000 and < 30000)
            return NostrEventKindCategory.Ephemeral;

        if (kind is >= 30000 and < 40000)
            return NostrEventKindCategory.Addressable;

        return NostrEventKindCategory.Regular;
    }

    public static NostrEventKindCategory Classify(this NostrEvent evt) => Classify(evt.Kind);
}
