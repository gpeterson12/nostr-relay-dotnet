using Microsoft.EntityFrameworkCore;
using NostrRelay.Core;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// Filter-to-LINQ translation (Section 3.4), the EF era's counterpart to the old
/// <c>SqliteFilterSqlBuilder</c>'s hand-written SQL.
///
/// Matching is exact-equality throughout for <c>ids</c>/<c>authors</c>, not prefix
/// matching, per the original builder's own stated rationale: current NIP-01 text
/// requires "exact 64-character lowercase hex values" for these fields. That's why this
/// uses plain <c>Contains</c> (translates to SQL <c>IN</c>) rather than a per-item
/// <c>StartsWith</c> expression. <c>Contains</c> against a closure-captured list is also
/// the one collection-membership pattern EF Core reliably translates to SQL, no need for
/// the hand-built expression tree the Postgres builder uses for prefix matching.
/// </summary>
internal static class SqliteEventQueryBuilder
{
    public static IQueryable<NostrEventEntity> Build(SqliteNostrRelayDbContext context, NostrFilter filter, long nowUnixSeconds)
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
