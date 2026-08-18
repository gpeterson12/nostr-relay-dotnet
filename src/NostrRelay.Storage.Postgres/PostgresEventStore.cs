using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Postgres implementation of <see cref="IEventStore"/> (Section 5.2), now backed by EF
/// Core instead of Dapper. One short-lived <see cref="NostrRelayDbContext"/> per operation,
/// created from a <see cref="PooledDbContextFactory{TContext}"/>: the direct EF analogue of
/// the old "new connection per operation" pattern, since Npgsql's built-in connection
/// pooling (which that pattern relied on) sits underneath EF's context pooling too.
///
/// The one structurally different piece from a hypothetical SqliteEventStore migration:
/// replaceable/addressable writes still use a Postgres advisory lock
/// (<see cref="SaveKeyedAsync"/>), now taken via <c>ExecuteSqlInterpolatedAsync</c> against
/// an EF-managed transaction rather than a raw Dapper command. Same reasoning as before:
/// only writers targeting the same key ever contend.
/// </summary>
public sealed class PostgresEventStore : IEventStore
{
    private readonly string _connectionString;
    private readonly PooledDbContextFactory<NostrRelayDbContext> _contextFactory;

    public PostgresEventStore(string connectionString)
    {
        _connectionString = connectionString;

        var optionsBuilder = new DbContextOptionsBuilder<NostrRelayDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        _contextFactory = new PooledDbContextFactory<NostrRelayDbContext>(optionsBuilder.Options);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await PostgresDatabaseProvisioner.EnsureDatabaseExistsAsync(_connectionString, ct);

        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        await context.Database.MigrateAsync(ct);
    }

    public async Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct)
    {
        NostrEventKindCategory category = evt.Classify();

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
        // EF/SaveChanges has no native "ON CONFLICT DO NOTHING"; a unique-violation on
        // save is caught and treated as the duplicate signal instead, same outcome as the
        // old ON CONFLICT (id) DO NOTHING, one round trip either way.
        context.Events.Add(evt.ToEntity());
        context.EventTags.AddRange(evt.ToTagEntities());

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return PersistResult.Duplicate();
        }

        return PersistResult.Stored();
    }

    private static Task<PersistResult> SaveReplaceableAsync(NostrRelayDbContext context, NostrEvent evt, CancellationToken ct) =>
        SaveKeyedAsync(
            context, evt, ct,
            lockKey: $"replaceable:{evt.Pubkey}:{evt.Kind}",
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind));

    private static Task<PersistResult> SaveAddressableAsync(NostrRelayDbContext context, NostrEvent evt, CancellationToken ct)
    {
        var dTag = evt.GetFirstTagValue("d") ?? "";

        return SaveKeyedAsync(
            context, evt, ct,
            lockKey: $"addressable:{evt.Pubkey}:{evt.Kind}:{dTag}",
            lookup: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag),
            delete: q => q.Where(e => e.Pubkey == evt.Pubkey && e.Kind == evt.Kind && e.DTag == dTag));
    }

    /// <summary>
    /// Shared upsert-by-key logic for replaceable and addressable events (Section 3.3), the
    /// EF counterpart of the old Dapper <c>SaveKeyedAsync</c>. Still lookup-then-delete-
    /// then-insert rather than a native upsert, same rationale as before: this table's
    /// primary key is the Nostr event id, which changes with every version of a
    /// replaceable/addressable event, so there is no single row to "update in place".
    /// <see cref="ExecuteDeleteAsync{TSource}"/> runs against the same ambient transaction
    /// as the lookup and the eventual insert, so the whole read-check-delete-insert
    /// sequence is atomic per key, exactly as the raw-SQL version was.
    /// </summary>
    private static async Task<PersistResult> SaveKeyedAsync(
        NostrRelayDbContext context,
        NostrEvent evt,
        CancellationToken ct,
        string lockKey,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> lookup,
        Func<IQueryable<NostrEventEntity>, IQueryable<NostrEventEntity>> delete)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
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
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public async IAsyncEnumerable<NostrEvent> QueryAsync(
        IReadOnlyList<NostrFilter> filters, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filters.Count == 0)
            yield break;

        await using NostrRelayDbContext context = await _contextFactory.CreateDbContextAsync(ct);
        var seenIds = new HashSet<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (NostrFilter filter in filters)
        {
            var query = PostgresEventQueryBuilder.Build(context, filter, now)
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

        return await PostgresEventQueryBuilder.Build(context, filter, now).CountAsync(ct);
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
