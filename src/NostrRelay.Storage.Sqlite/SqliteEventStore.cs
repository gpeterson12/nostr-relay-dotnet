using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;
using NostrRelay.Storage.Ef;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IEventStore"/> (Section 5.2), backed by EF Core. The
/// <see cref="IDbContextFactory{TContext}"/> is constructor-injected rather than built
/// inside this class: the app registers it via
/// <c>AddPooledDbContextFactory&lt;SqliteNostrRelayDbContext&gt;</c> in <c>Program.cs</c>,
/// including the <c>foreign_keys</c> connection-string setting, so this class stays an
/// ordinary DI-resolvable singleton, matching <c>PostgresEventStore</c>.
///
/// <c>journal_mode = WAL</c> is set once in <see cref="InitializeAsync"/> rather than per
/// connection: it persists in the database file itself.
///
/// No provider-level retry-on-failure is configured, unlike <c>PostgresEventStore</c>.
/// That's deliberate: SQLite's actual transient-failure mode under concurrent writers is
/// <c>SQLITE_BUSY</c> when a second <c>BEGIN IMMEDIATE</c> contends with an in-progress
/// write, and <see cref="Microsoft.Data.Sqlite"/> already handles that at the connection
/// level via its "Default Timeout" setting, which SQLite's own busy handler uses to wait
/// and retry internally before ever surfacing <c>SQLITE_BUSY</c> as an exception. There is
/// no network involved for an embedded database, so an EF execution-strategy retry loop on
/// top of the busy timeout would add nothing.
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
        // hot, frequently-hit path carries real overhead that a plain existence check
        // avoids.
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
            // Reached when another writer inserted the same id in the window between the
            // check above and this insert. Rare in normal operation, but a real race:
            // EventStoreContractTests covers it explicitly with concurrent writers.
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
    /// Lookup-then-delete-then-insert rather than a native upsert: this table's primary key
    /// is the Nostr event id, which changes with every version, so there is no single row to
    /// update in place.
    ///
    /// Three lifetime details matter here and are easy to get wrong:
    ///
    /// 1. The transaction is opened directly on the underlying <see cref="SqliteConnection"/>
    ///    with <c>deferred: false</c> (BEGIN IMMEDIATE), rather than through EF's own
    ///    transaction API. A deferred BEGIN takes no write lock until the first write
    ///    statement, which would reopen the exact race this closes: two concurrent writers
    ///    could both complete the lookup below believing they are first.
    ///
    /// 2. The connection is opened by calling <c>OpenAsync</c> on the raw
    ///    <see cref="System.Data.Common.DbConnection"/>, deliberately <i>not</i> through
    ///    <c>Database.OpenConnectionAsync</c>. This looks backwards and is not.
    ///    <see cref="Microsoft.EntityFrameworkCore.Storage.RelationalConnection"/> keeps an
    ///    open count and closes the underlying connection only if it opened the connection
    ///    itself. Opening it raw leaves that count at zero, so EF never considers itself the
    ///    owner and never closes a connection this method is still using.
    ///
    ///    Route the open through EF instead and ownership becomes ambiguous: EF believes it
    ///    opened the connection for the transaction and closes it during transaction
    ///    teardown, while the externally-owned <see cref="SqliteTransaction"/> is still
    ///    alive. The connection then returns to Microsoft.Data.Sqlite's pool with a native
    ///    transaction still open on the handle, and the next caller to be handed that
    ///    pooled connection fails with either "cannot start a transaction within a
    ///    transaction" or "unable to delete/modify collation sequence due to active
    ///    statements". Both surface only under concurrent writers, which is why the
    ///    concurrency contract tests exist.
    ///
    ///    There is no matching close here for the same reason: the pooled context closes the
    ///    connection when it resets state on return to the pool.
    ///
    /// 3. The raw <see cref="SqliteTransaction"/> is disposed by this method, via
    ///    <c>await using</c>. <see cref="DatabaseFacade.UseTransactionAsync"/> wraps an
    ///    externally-supplied transaction as not-owned, so disposing the
    ///    <see cref="IDbContextTransaction"/> alone does <i>not</i> dispose the underlying
    ///    one. Without this, any exception between BEGIN and COMMIT (a failed
    ///    <c>SaveChangesAsync</c>, a cancellation) would leave an open write transaction on
    ///    a connection heading back into the pool. Disposal rolls back an uncommitted
    ///    transaction, which is the behavior we want on every failure path. Declaration
    ///    order matters: the EF wrapper is declared after the raw transaction, so it
    ///    disposes first and the rollback happens last.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        SqliteNostrRelayDbContext context,
        NostrEvent evt,
        CancellationToken ct,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> lookup,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> delete)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using SqliteTransaction sqliteTransaction = connection.BeginTransaction(deferred: false);

        // UseTransactionAsync's return type is nullable because it mirrors its (nullable)
        // input: passing null clears any transaction EF currently associates with the
        // connection and returns null. sqliteTransaction is never null here, so this can't
        // actually happen; the guard fails loudly right here rather than letting
        // SaveChangesAsync below silently attempt its own implicit transaction on a
        // connection that already has BEGIN IMMEDIATE open.
        await using IDbContextTransaction transaction =
            await context.Database.UseTransactionAsync(sqliteTransaction, ct)
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

        // Results are fully materialized before the context (and its underlying connection)
        // is released, rather than yielding directly from the open context. Yielding from an
        // open context would hold the connection open for as long as the slowest downstream
        // consumer takes to drain, and the server's backpressure design (Section 5.3) means a
        // slow subscriber's outbound channel can back up for a while. Each filter's own Limit
        // (default 500) bounds how much this buffers in memory.
        List<NostrEvent> results;

        await using (SqliteNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct))
        {
            var seenIds = new HashSet<string>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            results = [];

            // Filters are OR'd (Section 3.4): each is queried independently, respecting its
            // own `limit`, and results are deduplicated by id as they're collected.
            foreach (NostrFilter filter in filters)
            {
                var query = NostrEventQueryBuilder.Build(context, filter, now)
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

        return await NostrEventQueryBuilder.Build(context, filter, now).CountAsync(ct);
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

        // "kind != 5" enforces NIP-09's "deletion request against a deletion request has no
        // effect": a kind-5 row is never deletable through this path.
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