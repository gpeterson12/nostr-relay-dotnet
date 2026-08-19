using NostrRelay.Core;
using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Ef;

/// <summary>
/// Filter-to-LINQ translation (Section 3.4), now defined once for every provider. The two
/// previous copies were already identical line for line; the only reason they existed
/// separately was that each was typed against its own provider's context, which the shared
/// <see cref="NostrRelayDbContext"/> base removes.
///
/// Matching is exact equality for <c>ids</c> and <c>authors</c>, not prefix matching:
/// NIP-01 requires those filter lists to contain exact 64-character lowercase hex values.
/// <c>Contains</c> against a closure-captured collection is the membership pattern both
/// providers translate natively (SQL <c>IN</c> on SQLite, <c>= ANY(...)</c> on Npgsql), so
/// no hand-built expression trees are needed.
/// </summary>
public static class NostrEventQueryBuilder
{
    public static IQueryable<NostrEventEntity> Build(NostrRelayDbContext context, NostrFilter filter, long nowUnixSeconds)
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
        // stored." Enforced on every query unconditionally, independent of whether the
        // background sweep has run recently.
        return query.Where(e => e.ExpiresAt == null || e.ExpiresAt > nowUnixSeconds);
    }
}
