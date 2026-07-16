using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IEventStore"/> (Section 5.2), first of the two
/// planned providers (Postgres follows in Milestone 6 against the same contract test
/// suite). Opens a fresh connection per operation rather than pooling, per the spec's
/// stated rationale for SQLite's concurrency model.
/// </summary>
public sealed class SqliteEventStore(string connectionString) : IEventStore
{
    /// <summary>Creates the schema if it doesn't already exist. Call once at startup
    /// before the store is used (idempotent, safe to call on every process start).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
    }

    public async Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct)
    {
        NostrEventKindCategory category = evt.Classify();

        // Ephemeral events never touch storage at all (Section 3.3): no connection, no I/O.
        if (category == NostrEventKindCategory.Ephemeral)
            return PersistResult.Ephemeral();

        await using SqliteConnection connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);

        return category switch
        {
            NostrEventKindCategory.Regular => await SaveRegularAsync(connection, evt, ct),
            NostrEventKindCategory.Replaceable => await SaveReplaceableAsync(connection, evt, ct),
            NostrEventKindCategory.Addressable => await SaveAddressableAsync(connection, evt, ct),
            _ => throw new InvalidOperationException($"unhandled kind category: {category}"),
        };
    }

    private static async Task<PersistResult> SaveRegularAsync(SqliteConnection connection, NostrEvent evt, CancellationToken ct)
    {
        // id is the primary key, so a regular event is naturally deduplicated by insert:
        // INSERT OR IGNORE affects 0 rows if the id already exists.
        var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO events (id, pubkey, created_at, kind, tags, content, sig, expires_at, d_tag)
            VALUES (@Id, @Pubkey, @CreatedAt, @Kind, @Tags, @Content, @Sig, @ExpiresAt, @DTag)
            """,
            BuildInsertParameters(evt),
            cancellationToken: ct));

        if (rowsAffected == 0)
            return PersistResult.Duplicate();

        await InsertTagsAsync(connection, evt, ct);
        return PersistResult.Stored();
    }

    private static Task<PersistResult> SaveReplaceableAsync(SqliteConnection connection, NostrEvent evt, CancellationToken ct) =>
        SaveKeyedAsync(
            connection, evt, ct,
            lookupSql: "SELECT id AS Id, created_at AS CreatedAt FROM events WHERE pubkey = @Pubkey AND kind = @Kind",
            lookupParams: new { evt.Pubkey, evt.Kind },
            deleteSql: "DELETE FROM events WHERE pubkey = @Pubkey AND kind = @Kind",
            deleteParams: new { evt.Pubkey, evt.Kind });

    private static Task<PersistResult> SaveAddressableAsync(SqliteConnection connection, NostrEvent evt, CancellationToken ct)
    {
        var dTag = evt.GetFirstTagValue("d") ?? "";

        return SaveKeyedAsync(
            connection, evt, ct,
            lookupSql: "SELECT id AS Id, created_at AS CreatedAt FROM events WHERE pubkey = @Pubkey AND kind = @Kind AND d_tag = @DTag",
            lookupParams: new { evt.Pubkey, evt.Kind, DTag = dTag },
            deleteSql: "DELETE FROM events WHERE pubkey = @Pubkey AND kind = @Kind AND d_tag = @DTag",
            deleteParams: new { evt.Pubkey, evt.Kind, DTag = dTag });
    }

    /// <summary>
    /// Shared upsert-by-key logic for replaceable and addressable events (Section 3.3):
    /// only the latest event per key is retained. Per NIP-01, ties on <c>created_at</c>
    /// are broken by keeping the lowest id in lexical order.
    ///
    /// Wrapped in <c>BEGIN IMMEDIATE</c> (raw SQL rather than the ADO.NET transaction
    /// object, for a guaranteed write lock acquired up front) so the lookup-then-decide
    /// isn't racy under concurrent writers targeting the same (pubkey, kind[, d_tag]) key.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        SqliteConnection connection,
        NostrEvent evt,
        CancellationToken ct,
        string lookupSql,
        object lookupParams,
        string deleteSql,
        object deleteParams)
    {
        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE;", cancellationToken: ct));

        try
        {
            var existing = await connection.QuerySingleOrDefaultAsync<ExistingKeyRow>(
                new CommandDefinition(lookupSql, lookupParams, cancellationToken: ct));

            if (existing is not null)
            {
                var incomingWins =
                    evt.CreatedAt > existing.CreatedAt ||
                    (evt.CreatedAt == existing.CreatedAt && string.CompareOrdinal(evt.Id, existing.Id) < 0);

                if (!incomingWins)
                {
                    await connection.ExecuteAsync(new CommandDefinition("COMMIT;", cancellationToken: ct));
                    return PersistResult.Superseded();
                }

                await connection.ExecuteAsync(new CommandDefinition(deleteSql, deleteParams, cancellationToken: ct));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO events (id, pubkey, created_at, kind, tags, content, sig, expires_at, d_tag)
                VALUES (@Id, @Pubkey, @CreatedAt, @Kind, @Tags, @Content, @Sig, @ExpiresAt, @DTag)
                """,
                BuildInsertParameters(evt),
                cancellationToken: ct));

            await InsertTagsAsync(connection, evt, ct);

            await connection.ExecuteAsync(new CommandDefinition("COMMIT;", cancellationToken: ct));
            return PersistResult.Stored();
        }
        catch
        {
            await connection.ExecuteAsync("ROLLBACK;");
            throw;
        }
    }

    private static async Task InsertTagsAsync(SqliteConnection connection, NostrEvent evt, CancellationToken ct)
    {
        // NIP-01: only single-letter (a-zA-Z) tag names are indexed, and only each tag's
        // first value.
        var rows = evt.Tags
            .Where(tag => tag.Count >= 2 && tag[0].Length == 1 && char.IsAsciiLetter(tag[0][0]))
            .Select(tag => new { EventId = evt.Id, TagName = tag[0], TagValue = tag[1] })
            .ToList();

        if (rows.Count == 0)
            return;

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO event_tags (event_id, tag_name, tag_value) VALUES (@EventId, @TagName, @TagValue)",
            rows,
            cancellationToken: ct));
    }

    private static object BuildInsertParameters(NostrEvent evt)
    {
        NostrEventKindCategory category = evt.Classify();

        return new
        {
            evt.Id,
            evt.Pubkey,
            evt.CreatedAt,
            evt.Kind,
            Tags = JsonSerializer.Serialize(evt.Tags),
            evt.Content,
            evt.Sig,
            ExpiresAt = ExtractExpiresAt(evt),
            DTag = category == NostrEventKindCategory.Addressable ? (evt.GetFirstTagValue("d") ?? "") : null,
        };
    }

    /// <summary>NIP-40: expiration is carried as an <c>["expiration", "&lt;unix-ts&gt;"]</c>
    /// tag, not a first-class event field. Extracted at save time so the indexed
    /// <c>expires_at</c> column is populated from day one, ahead of full NIP-40 sweep
    /// logic (Milestone 9).</summary>
    private static long? ExtractExpiresAt(NostrEvent evt)
    {
        var raw = evt.GetFirstTagValue("expiration");
        return raw is not null && long.TryParse(raw, out var value) ? value : null;
    }

    public async IAsyncEnumerable<NostrEvent> QueryAsync(
        IReadOnlyList<NostrFilter> filters, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filters.Count == 0)
            yield break;

        await using SqliteConnection connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);

        // Filters are OR'd (Section 3.4): each is queried independently, respecting its
        // own `limit`, and results are deduplicated by id as they stream out. This does
        // not produce one globally-sorted merge across filters, each filter's own slice
        // is most-recent-first, but filters are concatenated rather than merge-sorted
        // together. Acceptable for now; a merge-sorted cross-filter stream is a
        // candidate optimization if benchmarking (Section 4.1) shows it matters.
        var seenIds = new HashSet<string>();

        for (var i = 0; i < filters.Count; i++)
        {
            var prefix = $"f{i}";
            (var whereClause, DynamicParameters parameters) = SqliteFilterSqlBuilder.Build(filters[i], prefix);

            var limitParam = $"{prefix}_limit";
            parameters.Add(limitParam, filters[i].Limit ?? 500);

            var sql = $"""
                SELECT id AS Id, pubkey AS Pubkey, created_at AS CreatedAt, kind AS Kind, tags AS Tags, content AS Content, sig AS Sig
                FROM events e
                WHERE {whereClause}
                ORDER BY created_at DESC, id ASC
                LIMIT @{limitParam}
                """;

            var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
            await using DbDataReader reader = await connection.ExecuteReaderAsync(command);
            var parse = reader.GetRowParser<EventRow>();

            while (await reader.ReadAsync(ct))
            {
                EventRow row = parse(reader);
                if (seenIds.Add(row.Id))
                    yield return row.ToNostrEvent();
            }
        }
    }

    public async Task<long> CountAsync(NostrFilter filter, CancellationToken ct)
    {
        await using SqliteConnection connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
        (var whereClause, DynamicParameters parameters) = SqliteFilterSqlBuilder.Build(filter, "f");

        var sql = $"SELECT COUNT(*) FROM events e WHERE {whereClause}";
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using SqliteConnection connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events WHERE id IN @Ids", new { Ids = ids }, cancellationToken: ct));
    }

    public async Task DeleteExpiredEventsAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events WHERE expires_at IS NOT NULL AND expires_at <= @Now",
            new { Now = nowUnix },
            cancellationToken: ct));
    }

    private sealed class ExistingKeyRow
    {
        public string Id { get; set; } = "";
        public long CreatedAt { get; set; }
    }
}
