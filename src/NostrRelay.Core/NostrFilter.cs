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
    /// Prefix matching applies to Ids and Authors per NIP-01 (a filter value matches if the
    /// event field starts with it).
    /// </summary>
    public bool Matches(NostrEvent evt)
    {
        if (Ids is { Count: > 0 } && !Ids.Any(prefix => evt.Id.StartsWith(prefix, StringComparison.Ordinal)))
            return false;

        if (Authors is { Count: > 0 } && !Authors.Any(prefix => evt.Pubkey.StartsWith(prefix, StringComparison.Ordinal)))
            return false;

        if (Kinds is { Count: > 0 } && !Kinds.Contains(evt.Kind))
            return false;

        if (Since is { } since && evt.CreatedAt < since)
            return false;

        if (Until is { } until && evt.CreatedAt > until)
            return false;

        if (TagFilters is not { Count: > 0 })
            return true;
        
        foreach (var (tagName, allowedValues) in TagFilters)
        {
            if (allowedValues.Count == 0)
                continue;

            var eventHasMatch = evt.Tags.Any(tag =>
                tag is [{ Length: 1 }, _, ..] &&
                tag[0][0] == tagName &&
                allowedValues.Contains(tag[1]));

            if (!eventHasMatch)
                return false;
        }

        return true;
    }
}
