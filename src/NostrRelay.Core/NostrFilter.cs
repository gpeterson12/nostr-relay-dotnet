namespace NostrRelay.Core;

/// <summary>
/// A single filter from a REQ message (Section 3.4). Multiple filters within a REQ
/// are OR'd together by the caller; all conditions within one filter are AND'd here.
/// All properties are optional (null/empty means "no constraint on this dimension").
/// </summary>
public sealed record NostrFilter
{
    public IReadOnlyList<string>? Ids { get; init; }

    public IReadOnlyList<string>? Authors { get; init; }

    public IReadOnlyList<int>? Kinds { get; init; }

    /// <summary>
    /// Single-letter tag filters, e.g. "#e" -> ["&lt;id1&gt;", "&lt;id2&gt;"], "#p" -> ["&lt;pubkey&gt;"].
    /// Keyed by the bare letter (no leading '#').
    /// </summary>
    public IReadOnlyDictionary<char, IReadOnlyList<string>>? TagFilters { get; init; }

    public long? Since { get; init; }

    public long? Until { get; init; }

    public int? Limit { get; init; }

    /// <summary>
    /// Evaluates whether <paramref name="evt"/> matches every constraint present on this filter.
    /// Per the current NIP-01 text, <c>ids</c> and <c>authors</c> filter lists "MUST contain
    /// exact 64-character lowercase hex values", so matching here is exact equality, not
    /// prefix matching (an earlier revision of this method used prefix matching, which
    /// matched an older understanding of the spec; corrected after re-checking the live
    /// NIP-01 document).
    /// </summary>
    public bool Matches(NostrEvent evt)
    {
        if (Ids is { Count: > 0 } && !Ids.Contains(evt.Id))
            return false;

        if (Authors is { Count: > 0 } && !Authors.Contains(evt.Pubkey))
            return false;

        if (Kinds is { Count: > 0 } && !Kinds.Contains(evt.Kind))
            return false;

        if (Since is { } since && evt.CreatedAt < since)
            return false;

        if (Until is { } until && evt.CreatedAt > until)
            return false;

        if (TagFilters is { Count: > 0 })
        {
            foreach (var (tagName, allowedValues) in TagFilters)
            {
                if (allowedValues.Count == 0)
                    continue;

                var eventHasMatch = evt.Tags.Any(tag =>
                    tag.Count >= 2 &&
                    tag[0].Length == 1 &&
                    tag[0][0] == tagName &&
                    allowedValues.Contains(tag[1]));

                if (!eventHasMatch)
                    return false;
            }
        }

        return true;
    }
}