using Dapper;
using NostrRelay.Core;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// Filter-to-SQL translation (Section 3.4). Every condition within a filter is AND'd;
/// tag filters use an EXISTS subquery against the normalized <c>event_tags</c> table
/// rather than the JSON <c>tags</c> column, since that's what the tag indexes are built
/// on. Matching is exact-equality throughout, matching the current NIP-01 text ("The
/// ids, authors, #e and #p filter lists MUST contain exact 64-character lowercase hex
/// values") rather than the prefix matching an earlier draft of this codebase assumed.
///
/// Parameters are namespaced by <paramref name="paramPrefix"/> so multiple filters from
/// the same REQ can be combined into parameter sets without name collisions.
/// </summary>
internal static class SqliteFilterSqlBuilder
{
    public static (string WhereClause, DynamicParameters Parameters) Build(NostrFilter filter, string paramPrefix)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (filter.Ids is { Count: > 0 })
        {
            var p = $"{paramPrefix}_ids";
            conditions.Add($"e.id IN @{p}");
            parameters.Add(p, filter.Ids);
        }

        if (filter.Authors is { Count: > 0 })
        {
            var p = $"{paramPrefix}_authors";
            conditions.Add($"e.pubkey IN @{p}");
            parameters.Add(p, filter.Authors);
        }

        if (filter.Kinds is { Count: > 0 })
        {
            var p = $"{paramPrefix}_kinds";
            conditions.Add($"e.kind IN @{p}");
            parameters.Add(p, filter.Kinds);
        }

        if (filter.Since is { } since)
        {
            var p = $"{paramPrefix}_since";
            conditions.Add($"e.created_at >= @{p}");
            parameters.Add(p, since);
        }

        if (filter.Until is { } until)
        {
            var p = $"{paramPrefix}_until";
            conditions.Add($"e.created_at <= @{p}");
            parameters.Add(p, until);
        }

        if (filter.TagFilters is { Count: > 0 })
        {
            var tagIndex = 0;
            foreach (var (tagName, values) in filter.TagFilters)
            {
                if (values.Count == 0)
                    continue;

                var nameParam = $"{paramPrefix}_tagname_{tagIndex}";
                var valuesParam = $"{paramPrefix}_tagvalues_{tagIndex}";

                conditions.Add($"""
                    EXISTS (
                        SELECT 1 FROM event_tags et
                        WHERE et.event_id = e.id AND et.tag_name = @{nameParam} AND et.tag_value IN @{valuesParam}
                    )
                    """);

                parameters.Add(nameParam, tagName.ToString());
                parameters.Add(valuesParam, values);
                tagIndex++;
            }
        }

        var whereClause = conditions.Count > 0 ? string.Join(" AND ", conditions) : "1=1";
        return (whereClause, parameters);
    }
}
