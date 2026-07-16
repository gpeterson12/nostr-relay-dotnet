using Dapper;
using NostrRelay.Core;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Filter-to-SQL translation (Section 3.4), kept structurally parallel to
/// <c>SqliteFilterSqlBuilder</c> rather than sharing an abstraction across providers, per
/// the spec's explicit allowance ("shared or parallel-but-tested component", Section 3.4).
///
/// One deliberate divergence from the SQLite builder: list-membership conditions use
/// <c>= ANY(@param)</c> rather than <c>IN @param</c>. Npgsql natively binds a List/array
/// parameter as a single Postgres array value, and Dapper's usual "expand IN @param into
/// IN (@p1, @p2, ...)" text rewriting doesn't reliably kick in against that, producing
/// literally <c>IN $1</c>, which Postgres rejects outright (IN needs a parenthesized list,
/// not a single bound value). <c>= ANY(@param)</c> is the standard Npgsql+Dapper pattern
/// for exactly this: Postgres's ANY() operator is built to take a single array value
/// directly. SQLite has no equivalent native array binding, so its builder keeps IN @param,
/// which works correctly there; this isn't an oversight on that side, the two engines
/// genuinely want different SQL for the same logical condition.
/// </summary>
internal static class PostgresFilterSqlBuilder
{
    public static (string WhereClause, DynamicParameters Parameters) Build(NostrFilter filter, string paramPrefix)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (filter.Ids is { Count: > 0 })
        {
            var p = $"{paramPrefix}_ids";
            conditions.Add($"e.id = ANY(@{p})");
            parameters.Add(p, filter.Ids);
        }

        if (filter.Authors is { Count: > 0 })
        {
            var p = $"{paramPrefix}_authors";
            conditions.Add($"e.pubkey = ANY(@{p})");
            parameters.Add(p, filter.Authors);
        }

        if (filter.Kinds is { Count: > 0 })
        {
            var p = $"{paramPrefix}_kinds";
            conditions.Add($"e.kind = ANY(@{p})");
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
                        WHERE et.event_id = e.id AND et.tag_name = @{nameParam} AND et.tag_value = ANY(@{valuesParam})
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