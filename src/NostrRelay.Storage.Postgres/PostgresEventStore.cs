using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Npgsql;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Postgres implementation of <see cref="IEventStore"/> (Section 5.2), the second provider
/// against the same contract test suite SqliteEventStore already passes. Opens a fresh
/// connection per operation, same as SqliteEventStore; for Npgsql specifically this is the
/// recommended pattern rather than an accommodation, Npgsql pools physical connections
/// behind the scenes per connection string, so "new NpgsqlConnection then dispose" is
/// cheap and is exactly how the driver's built-in pooling (Section 5.2) is meant to be used.
///
/// The one structurally different piece from SqliteEventStore: replaceable/addressable
/// writes use a Postgres advisory lock (<see cref="SaveKeyedAsync"/>) rather than SQLite's
/// whole-database <c>BEGIN IMMEDIATE</c>. This is a case where the two providers reach
/// correctness by genuinely different means rather than the same code translated verbatim,
/// which is exactly the kind of divergence Section 5.2 expects and the contract tests exist
/// to prove doesn't leak into different observable behavior.
/// </summary>
public sealed class PostgresEventStore(string connectionString) : IEventStore
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await PostgresDatabaseProvisioner.EnsureDatabaseExistsAsync(connectionString, ct);

        await using NpgsqlConnection connection = await OpenAsync(ct);
        await PostgresSchema.EnsureCreatedAsync(connection, ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct)
    {
        NostrEventKindCategory category = evt.Classify();

        if (category == NostrEventKindCategory.Ephemeral)
            return PersistResult.Ephemeral();

        await using NpgsqlConnection connection = await OpenAsync(ct);

        return category switch
        {
            NostrEventKindCategory.Regular => await SaveRegularAsync(connection, evt, ct),
            NostrEventKindCategory.Replaceable => await SaveReplaceableAsync(connection, evt, ct),
            NostrEventKindCategory.Addressable => await SaveAddressableAsync(connection, evt, ct),
            _ => throw new InvalidOperationException($"unhandled kind category: {category}"),
        };
    }

    private static async Task<PersistResult> SaveRegularAsync(NpgsqlConnection connection, NostrEvent evt, CancellationToken ct)
    {
        var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO events (id, pubkey, created_at, kind, tags, content, sig, expires_at, d_tag)
            VALUES (@Id, @Pubkey, @CreatedAt, @Kind, @Tags::jsonb, @Content, @Sig, @ExpiresAt, @DTag)
            ON CONFLICT (id) DO NOTHING
            """,
            BuildInsertParameters(evt),
            cancellationToken: ct));

        if (rowsAffected == 0)
            return PersistResult.Duplicate();

        await InsertTagsAsync(connection, evt, ct);
        return PersistResult.Stored();
    }

    private static Task<PersistResult> SaveReplaceableAsync(NpgsqlConnection connection, NostrEvent evt, CancellationToken ct) =>
        SaveKeyedAsync(
            connection, evt, ct,
            lockKey: $"replaceable:{evt.Pubkey}:{evt.Kind}",
            lookupSql: "SELECT id AS Id, created_at AS CreatedAt FROM events WHERE pubkey = @Pubkey AND kind = @Kind",
            lookupParams: new { evt.Pubkey, evt.Kind },
            deleteSql: "DELETE FROM events WHERE pubkey = @Pubkey AND kind = @Kind",
            deleteParams: new { evt.Pubkey, evt.Kind });

    private static Task<PersistResult> SaveAddressableAsync(NpgsqlConnection connection, NostrEvent evt, CancellationToken ct)
    {
        var dTag = evt.GetFirstTagValue("d") ?? "";

        return SaveKeyedAsync(
            connection, evt, ct,
            lockKey: $"addressable:{evt.Pubkey}:{evt.Kind}:{dTag}",
            lookupSql: "SELECT id AS Id, created_at AS CreatedAt FROM events WHERE pubkey = @Pubkey AND kind = @Kind AND d_tag = @DTag",
            lookupParams: new { evt.Pubkey, evt.Kind, DTag = dTag },
            deleteSql: "DELETE FROM events WHERE pubkey = @Pubkey AND kind = @Kind AND d_tag = @DTag",
            deleteParams: new { evt.Pubkey, evt.Kind, DTag = dTag });
    }

    /// <summary>
    /// Shared upsert-by-key logic for replaceable and addressable events (Section 3.3).
    /// Deliberately uses lookup-then-delete-then-insert (the same shape as
    /// SqliteEventStore) rather than Postgres's native <c>ON CONFLICT ... DO UPDATE</c>:
    /// this table's primary key <i>is</i> the Nostr event id, and a replaceable event's id
    /// changes with every version, so an upsert would need to change the row's primary key
    /// in place. Without <c>ON UPDATE CASCADE</c> on event_tags' foreign key, that would
    /// orphan the previous version's tag rows; adding <c>ON UPDATE CASCADE</c> would fix
    /// referential integrity but still leave the tag rows holding the *old* event's tag
    /// content under the *new* id, since a cascade only renames the foreign key, it doesn't
    /// replace the tag data. Delete-then-insert sidesteps both problems and keeps this
    /// provider's observable behavior identical to SQLite's, which is what the contract
    /// tests are actually verifying.
    ///
    /// The concurrency guard is genuinely different, though: a Postgres advisory lock
    /// scoped to this transaction and keyed on the exact replaceable/addressable identity,
    /// serializing concurrent writers targeting that one key, including the "no row exists
    /// yet" case that a `SELECT ... FOR UPDATE` can't protect (there's no row to lock).
    /// This is more surgical than SQLite's whole-database `BEGIN IMMEDIATE` write lock,
    /// only writers to the *same* key ever contend with each other.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        NpgsqlConnection connection,
        NostrEvent evt,
        CancellationToken ct,
        string lockKey,
        string lookupSql,
        object lookupParams,
        string deleteSql,
        object deleteParams)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@LockKey)::bigint)",
            new { LockKey = lockKey }, transaction, cancellationToken: ct));

        var existing = await connection.QuerySingleOrDefaultAsync<ExistingKeyRow>(
            new CommandDefinition(lookupSql, lookupParams, transaction, cancellationToken: ct));

        if (existing is not null)
        {
            var incomingWins =
                evt.CreatedAt > existing.CreatedAt ||
                (evt.CreatedAt == existing.CreatedAt && string.CompareOrdinal(evt.Id, existing.Id) < 0);

            if (!incomingWins)
            {
                await transaction.CommitAsync(ct);
                return PersistResult.Superseded();
            }

            await connection.ExecuteAsync(new CommandDefinition(deleteSql, deleteParams, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO events (id, pubkey, created_at, kind, tags, content, sig, expires_at, d_tag)
            VALUES (@Id, @Pubkey, @CreatedAt, @Kind, @Tags::jsonb, @Content, @Sig, @ExpiresAt, @DTag)
            """,
            BuildInsertParameters(evt),
            transaction,
            cancellationToken: ct));

        await InsertTagsAsync(connection, evt, ct, transaction);

        await transaction.CommitAsync(ct);
        return PersistResult.Stored();
    }

    private static async Task InsertTagsAsync(
        NpgsqlConnection connection, NostrEvent evt, CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        var rows = evt.Tags
            .Where(tag => tag.Count >= 2 && tag[0].Length == 1 && char.IsAsciiLetter(tag[0][0]))
            .Select(tag => new { EventId = evt.Id, TagName = tag[0], TagValue = tag[1] })
            .ToList();

        if (rows.Count == 0)
            return;

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO event_tags (event_id, tag_name, tag_value) VALUES (@EventId, @TagName, @TagValue)",
            rows,
            transaction,
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
            DTag = category == NostrEventKindCategory.Addressable ? evt.GetFirstTagValue("d") ?? "" : null,
        };
    }

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

        await using NpgsqlConnection connection = await OpenAsync(ct);
        var seenIds = new HashSet<string>();

        for (var i = 0; i < filters.Count; i++)
        {
            var prefix = $"f{i}";
            (var whereClause, DynamicParameters parameters) = PostgresFilterSqlBuilder.Build(filters[i], prefix);

            var limitParam = $"{prefix}_limit";
            parameters.Add(limitParam, filters[i].Limit ?? 500);

            var sql = $"""
                SELECT id AS Id, pubkey AS Pubkey, created_at AS CreatedAt, kind AS Kind, tags::text AS Tags, content AS Content, sig AS Sig
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
                if (seenIds.Add(row.Id.TrimEnd()))
                    yield return row.ToNostrEvent();
            }
        }
    }

    public async Task<long> CountAsync(NostrFilter filter, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await OpenAsync(ct);
        (var whereClause, DynamicParameters parameters) = PostgresFilterSqlBuilder.Build(filter, "f");

        var sql = $"SELECT COUNT(*) FROM events e WHERE {whereClause}";
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using NpgsqlConnection connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events WHERE id = ANY(@Ids)", new { Ids = ids }, cancellationToken: ct));
    }

    public async Task DeleteExpiredEventsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = await OpenAsync(ct);
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