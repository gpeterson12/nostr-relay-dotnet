using Microsoft.EntityFrameworkCore;
using NostrRelay.Core;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Filter-to-LINQ translation (Section 3.4), the EF era's counterpart to the old
/// <c>PostgresFilterSqlBuilder</c>'s hand-written SQL, kept structurally parallel to
/// <c>SqliteEventQueryBuilder</c> per the spec's explicit allowance for a "shared or
/// parallel-but-tested component" (Section 3.4).
///
/// Matching is exact-equality throughout for <c>ids</c>/<c>authors</c>, not prefix
/// matching. An earlier version of this file used a hand-built OR-chain of
/// <c>StartsWith</c> expressions here, inherited from <c>NostrFilter.Matches</c>'
/// in-memory prefix semantics rather than from what the original
/// <c>PostgresFilterSqlBuilder</c> actually did (<c>e.id = ANY(@ids)</c>, exact match).
/// Current NIP-01 text is explicit that the ids/authors/#e/#p filter lists "MUST contain
/// exact 64-character lowercase hex values", matching <c>SqliteFilterSqlBuilder</c>'s own
/// stated rationale for using <c>IN</c> rather than any prefix construct. <c>Contains</c>
/// against a closure-captured collection is also the pattern Npgsql's provider natively
/// translates to `= ANY(...)`, so ids/authors/kinds/tag-values all stay plain LINQ now,
/// no expression-tree building required.
/// </summary>
internal static class PostgresEventQueryBuilder
{
    public static IQueryable<NostrEventEntity> Build(PostgresNostrRelayDbContext context, NostrFilter filter, long nowUnixSeconds)
    {
        var query = context.Events.AsNoTracking();

        if (filter.Ids is { Count: > 0 } ids)
            query = query.Where(e => ids.Contains(e.Id));

        if (filter.Authors is { Count: > 0 } authors)
            query = query.Where(e => authors.Contains(e.Pubkey));

        if (filter.Kinds is { Count: > 0 } kinds)
            query = query.Where(e => kinds.Contains(e.Kind));

        if (filter.Since is { } since)
            query = query.Where(e => e.CreatedAt >= since);

        if (filter.Until is { } until)
            query = query.Where(e => e.CreatedAt <= until);

        if (filter.TagFilters is { Count: > 0 })
        {
            foreach (var (tagName, values) in filter.TagFilters)
            {
                if (values.Count == 0)
                    continue;

                var name = tagName.ToString();
                query = query.Where(e => context.EventTags.Any(t =>
                    t.EventId == e.Id && t.TagName == name && values.Contains(t.TagValue)));
            }
        }

        // NIP-40: "Relays SHOULD NOT send expired events to clients, even if they are
        // stored." Enforced on every query unconditionally, matching the old raw-SQL builder.
        return query.Where(e => e.ExpiresAt == null || e.ExpiresAt > nowUnixSeconds);
    }
}