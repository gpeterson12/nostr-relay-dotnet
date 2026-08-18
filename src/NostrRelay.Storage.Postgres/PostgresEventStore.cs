using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Postgres implementation of <see cref="IEventStore"/> (Section 5.2), backed by EF Core.
/// The <see cref="NpgsqlDataSource"/>, the <see cref="IDbContextFactory{TContext}"/>, and
/// <see cref="StorageOptions"/> are all constructor-injected rather than built inside this
/// class: the app registers the first two via <c>AddNpgsqlDataSource</c> and
/// <c>AddPooledDbContextFactory&lt;PostgresNostrRelayDbContext&gt;</c>, and the third via
/// <c>Configure&lt;StorageOptions&gt;</c> against the "Storage" configuration section (see
/// <c>Program.cs</c>), so this class stays an ordinary DI-resolvable singleton
/// (<c>AddSingleton&lt;IEventStore, PostgresEventStore&gt;()</c>) with no special
/// construction step of its own. Because the data source is owned and disposed by the
/// container (it's registered directly as a service), this class does not implement
/// <see cref="IDisposable"/> itself; disposing it here as well would double-dispose a
/// resource the container already manages.
///
/// Retry-on-failure is expected to be configured where the context factory is registered
/// (Program.cs), not here. The one structurally different piece from
/// <c>SqliteEventStore</c>, replaceable/addressable writes taking a Postgres advisory lock
/// inside an explicit transaction (<see cref="SaveKeyedAsync"/>), is wrapped in an EF
/// execution strategy so that explicit transaction and provider-level retry can coexist
/// safely: EF Core throws at runtime if a retrying provider is combined with an explicit
/// transaction that isn't run through <c>CreateExecutionStrategy().ExecuteAsync(...)</c>.
/// </summary>
public sealed class PostgresEventStore(
    IDbContextFactory<PostgresNostrRelayDbContext> contextFactory,
    IOptions<StorageOptions> storageOptions)
    : IEventStore
{
    private readonly string _connectionString = storageOptions.Value.ConnectionString
        ?? throw new InvalidOperationException("Storage:ConnectionString is required when Storage:Provider is \"Postgres\".");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await PostgresDatabaseProvisioner.EnsureDatabaseExistsAsync(_connectionString, ct);

        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await context.Database.MigrateAsync(ct);
    }

    public async Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct)
    {
        NostrEventKindCategory category = evt.Classify();

        if (category == NostrEventKindCategory.Ephemeral)
            return PersistResult.Ephemeral();

        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);

        return category switch
        {
            NostrEventKindCategory.Regular => await SaveRegularAsync(context, evt, ct),
            NostrEventKindCategory.Replaceable => await SaveReplaceableAsync(context, evt, ct),
            NostrEventKindCategory.Addressable => await SaveAddressableAsync(context, evt, ct),
            _ => throw new InvalidOperationException($"unhandled kind category: {category}"),
        };
    }

    private static async Task<PersistResult> SaveRegularAsync(PostgresNostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        // Duplicate publishes are a common, expected outcome in Nostr (clients routinely
        // re-broadcast the same event to multiple relays), so this checks for an existing
        // row by primary key up front rather than relying on a unique-violation exception
        // as the primary detection mechanism: exception-based control flow on a hot,
        // frequently-hit path carries real overhead (exception construction, unwinding,
        // Npgsql's exception translation) that a plain existence check avoids.
        var exists = await context.Events.AsNoTracking().AnyAsync(e => e.Id == evt.Id, ct);
        if (exists)
            return PersistResult.Duplicate();

        context.Events.Add(evt.ToEntity());
        context.EventTags.AddRange(evt.ToTagEntities());

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Only reachable if another writer inserted the same id in the window between
            // the check above and this insert. Rare, so the exception path here is a
            // correctness fallback, not the mechanism the common case goes through.
            return PersistResult.Duplicate();
        }

        return PersistResult.Stored();
    }

    private static Task<PersistResult> SaveReplaceableAsync(PostgresNostrRelayDbContext context, NostrEvent evt, CancellationToken ct) =>
        SaveKeyedAsync(
            context, evt, ct,
            lockKey: $"replaceable:{evt.Pubkey}:{evt.Kind}",
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind));

    private static Task<PersistResult> SaveAddressableAsync(PostgresNostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        var dTag = evt.GetFirstTagValue("d") ?? "";

        return SaveKeyedAsync(
            context, evt, ct,
            lockKey: $"addressable:{evt.Pubkey}:{evt.Kind}:{dTag}",
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag));
    }

    /// <summary>
    /// Shared upsert-by-key logic for replaceable and addressable events (Section 3.3).
    /// Still lookup-then-delete-then-insert rather than a native upsert, same rationale as
    /// before: this table's primary key is the Nostr event id, which changes with every
    /// version of a replaceable/addressable event, so there is no single row to "update in
    /// place". The whole read-check-delete-insert sequence runs inside an explicit
    /// transaction for atomicity per key, and that transaction is itself run through an EF
    /// execution strategy so it can be retried as a whole unit on a transient failure
    /// (required whenever a retrying provider is combined with an explicit transaction).
    /// The change tracker is cleared at the start of each attempt so a retried attempt
    /// starts from a clean slate rather than re-adding entities left over from a failed one.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        PostgresNostrRelayDbContext context,
        NostrEvent evt,
        CancellationToken ct,
        string lockKey,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> lookup,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> delete)
    {
        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await context.Database.BeginTransactionAsync(ct);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);

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
        });
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public async IAsyncEnumerable<NostrEvent> QueryAsync(
        IReadOnlyList<NostrFilter> filters, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filters.Count == 0)
            yield break;

        // Results are fully materialized before the context (and its pooled Npgsql
        // connection) is released, rather than yielding directly from the open context.
        // Yielding from an open context would hold the pooled connection open for as long
        // as the slowest downstream consumer takes to drain, and the server's own
        // backpressure design (Section 5.3) means a slow subscriber's outbound channel can
        // back up for a while, which risks starving the connection pool under high
        // concurrent connection counts. Each filter's own Limit (default 500) bounds how
        // much this buffers in memory.
        List<NostrEvent> results;

        await using (PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct))
        {
            var seenIds = new HashSet<string>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            results = [];

            foreach (NostrFilter filter in filters)
            {
                var query = PostgresEventQueryBuilder.Build(context, filter, now)
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
        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return await PostgresEventQueryBuilder.Build(context, filter, now).CountAsync(ct);
    }

    public async Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        await context.Events.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteEventsAuthoredByAsync(IEnumerable<string> eventIds, string authorPubkey, CancellationToken ct)
    {
        var ids = eventIds as IReadOnlyCollection<string> ?? eventIds.ToList();
        if (ids.Count == 0)
            return;

        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);

        // "kind != 5" enforces NIP-09's "deletion request against a deletion request has
        // no effect": a kind-5 row is never deletable through this path.
        await context.Events
            .Where(e => ids.Contains(e.Id) && e.Pubkey == authorPubkey && e.Kind != 5)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAddressableEventAsync(string pubkey, int kind, string dTag, long upToCreatedAt, CancellationToken ct)
    {
        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);

        await context.Events
            .Where(e => e.Pubkey == pubkey && e.Kind == kind && e.DTag == dTag && e.CreatedAt <= upToCreatedAt)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteExpiredEventsAsync(CancellationToken ct)
    {
        await using PostgresNostrRelayDbContext context = await contextFactory.CreateDbContextAsync(ct);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await context.Events
            .Where(e => e.ExpiresAt != null && e.ExpiresAt <= nowUnix)
            .ExecuteDeleteAsync(ct);
    }
}