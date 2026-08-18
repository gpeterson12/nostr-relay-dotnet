using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IEventStore"/> (Section 5.2), backed by EF Core. The
/// <see cref="IDbContextFactory{TContext}"/> is constructor-injected rather than built
/// inside this class: the app registers it via
/// <c>AddPooledDbContextFactory&lt;SqliteNostrRelayDbContext&gt;</c> in <c>Program.cs</c>,
/// including the <c>foreign_keys</c> connection-string setting that used to be built here,
/// so this class stays an ordinary DI-resolvable singleton with no special construction
/// order relative to the rest of the container, matching <c>PostgresEventStore</c>.
///
/// <c>journal_mode = WAL</c> is still set once in <see cref="InitializeAsync"/> rather than
/// via the connection string: it persists in the database file itself, so re-setting it per
/// operation (or per registration) was always just a cheap no-op, not a requirement.
///
/// No provider-level retry-on-failure is configured, unlike <c>PostgresEventStore</c>.
/// That's deliberate, not an oversight: SQLite's actual transient-failure mode under
/// concurrent writers is <c>SQLITE_BUSY</c> when a second <c>BEGIN IMMEDIATE</c> contends
/// with an in-progress write, and <see cref="Microsoft.Data.Sqlite"/> already handles that
/// at the connection level via its "Default Timeout" setting (30 seconds by default), which
/// SQLite's own busy handler uses to wait and retry internally before ever surfacing
/// <c>SQLITE_BUSY</c> as an exception. There's no SQLite-provider equivalent of Npgsql's
/// <c>EnableRetryOnFailure()</c> for other kinds of transient failures, since there's no
/// network involved for an embedded database, so an EF execution-strategy retry loop on top
/// of the busy timeout would add nothing.
/// </summary>
public sealed class SqliteEventStore(IDbContextFactory<SqliteNostrRelayDbContext> contextFactory)
    : IEventStore
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await context.Database.MigrateAsync(ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);
    }

    public async Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct)
    {
        NostrEventKindCategory category = evt.Classify();

        // Ephemeral events never touch storage at all (Section 3.3): no context, no I/O.
        if (category == NostrEventKindCategory.Ephemeral)
            return PersistResult.Ephemeral();

        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);

        return category switch
        {
            NostrEventKindCategory.Regular => await SaveRegularAsync(context, evt, ct),
            NostrEventKindCategory.Replaceable => await SaveReplaceableAsync(context, evt, ct),
            NostrEventKindCategory.Addressable => await SaveAddressableAsync(context, evt, ct),
            _ => throw new InvalidOperationException($"unhandled kind category: {category}"),
        };
    }

    private static async Task<PersistResult> SaveRegularAsync(SqliteNostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        // Duplicate publishes are a common, expected outcome in Nostr (clients routinely
        // re-broadcast the same event to multiple relays), so this checks for an existing
        // row by primary key up front rather than relying on a constraint-violation
        // exception as the primary detection mechanism: exception-based control flow on a
        // hot, frequently-hit path carries real overhead (exception construction,
        // unwinding, Sqlite's exception translation) that a plain existence check avoids.
        bool exists = await context.Events.AsNoTracking().AnyAsync(e => e.Id == evt.Id, ct);
        if (exists)
            return PersistResult.Duplicate();

        context.Events.Add(evt.ToEntity());
        context.EventTags.AddRange(evt.ToTagEntities());

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsConstraintViolation(ex))
        {
            // Only reachable if another writer inserted the same id in the window between
            // the check above and this insert. Rare, so the exception path here is a
            // correctness fallback, not the mechanism the common case goes through.
            return PersistResult.Duplicate();
        }

        return PersistResult.Stored();
    }

    private static Task<PersistResult> SaveReplaceableAsync(SqliteNostrRelayDbContext context, NostrEvent evt, CancellationToken ct) =>
        SaveKeyedAsync(
            context, evt, ct,
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind));

    private static Task<PersistResult> SaveAddressableAsync(SqliteNostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        var dTag = evt.GetFirstTagValue("d") ?? "";

        return SaveKeyedAsync(
            context, evt, ct,
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag));
    }

    /// <summary>
    /// Shared upsert-by-key logic for replaceable and addressable events (Section 3.3).
    /// Still lookup-then-delete-then-insert, same rationale as before: this table's
    /// primary key is the Nostr event id, which changes with every version, so there's no
    /// single row to "update in place".
    ///
    /// The transaction is opened directly on the underlying <see cref="SqliteConnection"/>
    /// with <c>deferred: false</c> (BEGIN IMMEDIATE), not via
    /// <see cref="DatabaseFacade.BeginTransactionAsync(CancellationToken)"/>, which only
    /// issues SQLite's default deferred BEGIN. Deferred would reopen exactly the race the
    /// original raw-SQL version closed: a write lock isn't actually taken until the first
    /// write statement, so two concurrent writers could both pass the lookup believing
    /// they're first. <see cref="DatabaseFacade.UseTransactionAsync"/> then hands that
    /// externally-opened transaction to EF so the LINQ lookup, the delete, and
    /// <c>SaveChangesAsync</c> all participate in the same one. No execution-strategy
    /// wrapping is needed around this transaction, unlike the Postgres store's equivalent:
    /// no retrying provider is configured here (see this class's doc comment), so there's
    /// nothing that would conflict with an explicit transaction.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        SqliteNostrRelayDbContext context,
        NostrEvent evt,
        CancellationToken ct,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> lookup,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> delete)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        SqliteTransaction sqliteTransaction = connection.BeginTransaction(deferred: false);

        // UseTransactionAsync's return type is nullable because it mirrors its (nullable)
        // input: passing null clears any transaction EF currently associates with the
        // connection and returns null. sqliteTransaction here is never null, so this can't
        // actually happen; the guard exists to fail loudly right here rather than let
        // SaveChangesAsync below silently attempt its own implicit transaction on a
        // connection that already has BEGIN IMMEDIATE open, which would surface as a
        // confusing SQLite error far from the real cause.
        await using IDbContextTransaction transaction = await context.Database.UseTransactionAsync(sqliteTransaction, ct)
            ?? throw new InvalidOperationException(
                "UseTransactionAsync returned null for a non-null SqliteTransaction; the keyed save can't proceed without EF enlisted in the BEGIN IMMEDIATE transaction.");

        var existing = await lookup(context.Events)
            .Select(e => new { e.Id, e.CreatedAt })
            .SingleOrDefaultAsync(ct);

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

            await delete(context.Events).ExecuteDeleteAsync(ct);
        }

        context.Events.Add(evt.ToEntity());
        context.EventTags.AddRange(evt.ToTagEntities());
        await context.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return PersistResult.Stored();
    }

    private static bool IsConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: 19 }; // SQLITE_CONSTRAINT

    public async IAsyncEnumerable<NostrEvent> QueryAsync(
        IReadOnlyList<NostrFilter> filters, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filters.Count == 0)
            yield break;

        // Results are fully materialized before the context (and its underlying
        // connection) is released, rather than yielding directly from the open context.
        // Yielding from an open context would hold the connection open for as long as the
        // slowest downstream consumer takes to drain, and the server's own backpressure
        // design (Section 5.3) means a slow subscriber's outbound channel can back up for a
        // while, which risks holding a pooled context (and, transitively, a write-capable
        // connection under WAL) longer than necessary. Each filter's own Limit (default
        // 500) bounds how much this buffers in memory.
        List<NostrEvent> results;

        await using (SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct))
        {
            var seenIds = new HashSet<string>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            results = [];

            // Filters are OR'd (Section 3.4): each is queried independently, respecting
            // its own `limit`, and results are deduplicated by id as they're collected,
            // same filter-by-filter concatenation (not a globally merge-sorted stream) as
            // before.
            foreach (NostrFilter filter in filters)
            {
                var query = SqliteEventQueryBuilder.Build(context, filter, now)
                    .OrderByDescending(e => e.CreatedAt)
                    .ThenBy(e => e.Id)
                    .Take(filter.Limit ?? 500);

                await foreach (NostrEventEntity entity in query.AsAsyncEnumerable().WithCancellation(ct))
                {
                    if (seenIds.Add(entity.Id))
                        results.Add(entity.ToDomain());
                }
            }
        }

        foreach (NostrEvent evt in results)
            yield return evt;
    }

    public async Task<long> CountAsync(NostrFilter filter, CancellationToken ct)
    {
        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return await SqliteEventQueryBuilder.Build(context, filter, now).CountAsync(ct);
    }

    public async Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await context.Events.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteEventsAuthoredByAsync(IEnumerable<string> eventIds, string authorPubkey, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);

        // "kind != 5" enforces NIP-09's "deletion request against a deletion request has
        // no effect": a kind-5 row is never deletable through this path.
        await context.Events
            .Where(e => ids.Contains(e.Id) && e.Pubkey == authorPubkey && e.Kind != 5)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAddressableEventAsync(string pubkey, int kind, string dTag, long upToCreatedAt, CancellationToken ct)
    {
        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);

        await context.Events
            .Where(e => e.Pubkey == pubkey && e.Kind == kind && e.DTag == dTag && e.CreatedAt <= upToCreatedAt)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteExpiredEventsAsync(CancellationToken ct)
    {
        await using SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await context.Events
            .Where(e => e.ExpiresAt != null && e.ExpiresAt <= nowUnix)
            .ExecuteDeleteAsync(ct);
    }
}