using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IEventStore"/> (Section 5.2), now backed by EF Core
/// instead of Dapper. One short-lived <see cref="NostrRelayDbContext"/> per operation,
/// created from a <see cref="PooledDbContextFactory{TContext}"/>, the direct EF analogue
/// of the old "new connection per operation" pattern (Section 5.2's stated rationale for
/// SQLite's concurrency model still applies underneath EF's pooling).
///
/// Two pragmas the old <c>SqliteConnectionFactory</c> set per connection still need
/// setting, just relocated:
/// <list type="bullet">
/// <item><c>foreign_keys</c> is set via the connection string
/// (<see cref="SqliteConnectionStringBuilder.ForeignKeys"/>), applied by
/// Microsoft.Data.Sqlite on every connection open, same effect as the old per-open
/// PRAGMA, without needing a raw command every time.</item>
/// <item><c>journal_mode = WAL</c> is set once in <see cref="InitializeAsync"/>: it
/// persists in the database file itself, so re-setting it per operation was always just a
/// cheap no-op, not a requirement.</item>
/// </list>
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly PooledDbContextFactory<NostrRelayDbContext> _contextFactory;

    public SqliteEventStore(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true };

        var optionsBuilder = new DbContextOptionsBuilder<NostrRelayDbContext>();
        optionsBuilder.UseSqlite(builder.ConnectionString);
        _contextFactory = new PooledDbContextFactory<NostrRelayDbContext>(optionsBuilder.Options);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context.Database.MigrateAsync(ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);
    }

    public async Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct)
    {
        NostrEventKindCategory category = evt.Classify();

        // Ephemeral events never touch storage at all (Section 3.3): no context, no I/O.
        if (category == NostrEventKindCategory.Ephemeral)
            return PersistResult.Ephemeral();

        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);

        return category switch
        {
            NostrEventKindCategory.Regular => await SaveRegularAsync(context, evt, ct),
            NostrEventKindCategory.Replaceable => await SaveReplaceableAsync(context, evt, ct),
            NostrEventKindCategory.Addressable => await SaveAddressableAsync(context, evt, ct),
            _ => throw new InvalidOperationException($"unhandled kind category: {category}"),
        };
    }

    private static async Task<PersistResult> SaveRegularAsync(NostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        // id is the primary key, so a regular event is naturally deduplicated by insert.
        // EF/SaveChanges has no "INSERT OR IGNORE"; a constraint violation on save is
        // caught and treated as the duplicate signal instead, same outcome, one round trip.
        context.Events.Add(evt.ToEntity());
        context.EventTags.AddRange(evt.ToTagEntities());

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsConstraintViolation(ex))
        {
            return PersistResult.Duplicate();
        }

        return PersistResult.Stored();
    }

    private static Task<PersistResult> SaveReplaceableAsync(NostrRelayDbContext context, NostrEvent evt, CancellationToken ct) =>
        SaveKeyedAsync(
            context, evt, ct,
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind));

    private static Task<PersistResult> SaveAddressableAsync(NostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        var dTag = evt.GetFirstTagValue("d") ?? "";

        return SaveKeyedAsync(
            context, evt, ct,
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag));
    }

    /// <summary>
    /// Shared upsert-by-key logic for replaceable and addressable events (Section 3.3),
    /// the EF counterpart of the old Dapper <c>SaveKeyedAsync</c>. Still lookup-then-
    /// delete-then-insert, same rationale as before: this table's primary key is the
    /// Nostr event id, which changes with every version, so there's no single row to
    /// "update in place".
    ///
    /// The transaction is opened directly on the underlying <see cref="SqliteConnection"/>
    /// with <c>deferred: false</c> (BEGIN IMMEDIATE), not via
    /// <see cref="DatabaseFacade.BeginTransactionAsync(CancellationToken)"/>, which only
    /// issues SQLite's default deferred BEGIN. Deferred would reopen exactly the race the
    /// original raw-SQL version closed: a write lock isn't actually taken until the first
    /// write statement, so two concurrent writers could both pass the lookup believing
    /// they're first. <see cref="DatabaseFacade.UseTransactionAsync"/> then hands that
    /// externally-opened transaction to EF so the LINQ lookup, the delete, and
    /// <c>SaveChangesAsync</c> all participate in the same one.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        NostrRelayDbContext context,
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

        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        var seenIds = new HashSet<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Filters are OR'd (Section 3.4): each is queried independently, respecting its
        // own `limit`, and results are deduplicated by id as they stream out, same
        // filter-by-filter concatenation (not a globally merge-sorted stream) as before.
        foreach (NostrFilter filter in filters)
        {
            var query = SqliteEventQueryBuilder.Build(context, filter, now)
                .OrderByDescending(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Take(filter.Limit ?? 500);

            await foreach (NostrEventEntity entity in query.AsAsyncEnumerable().WithCancellation(ct))
            {
                if (seenIds.Add(entity.Id))
                    yield return entity.ToDomain();
            }
        }
    }

    public async Task<long> CountAsync(NostrFilter filter, CancellationToken ct)
    {
        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return await SqliteEventQueryBuilder.Build(context, filter, now).CountAsync(ct);
    }

    public async Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context.Events.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteEventsAuthoredByAsync(IEnumerable<string> eventIds, string authorPubkey, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);

        // "kind != 5" enforces NIP-09's "deletion request against a deletion request has
        // no effect": a kind-5 row is never deletable through this path.
        await context.Events
            .Where(e => ids.Contains(e.Id) && e.Pubkey == authorPubkey && e.Kind != 5)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAddressableEventAsync(string pubkey, int kind, string dTag, long upToCreatedAt, CancellationToken ct)
    {
        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);

        await context.Events
            .Where(e => e.Pubkey == pubkey && e.Kind == kind && e.DTag == dTag && e.CreatedAt <= upToCreatedAt)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteExpiredEventsAsync(CancellationToken ct)
    {
        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await context.Events
            .Where(e => e.ExpiresAt != null && e.ExpiresAt <= nowUnix)
            .ExecuteDeleteAsync(ct);
    }
}