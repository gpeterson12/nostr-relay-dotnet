using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// EF Core context for the Postgres event store. Most schema detail (table/column names,
/// types, plain named indexes) lives as data annotations directly on
/// <see cref="NostrEventEntity"/>/<see cref="EventTagEntity"/> instead of here; only the
/// indexes with no attribute equivalent are configured below: the two partial unique
/// indexes (<c>uq_events_replaceable</c>, <c>uq_events_addressable</c>) need
/// <c>HasFilter</c>, a Postgres partial-index concept with no attribute form in EF Core.
///
/// One context per operation (Section 5.2's "connection per operation" carried over to EF):
/// <see cref="PostgresEventStore"/> creates and disposes an instance per call via a
/// <see cref="PooledDbContextFactory{TContext}"/>, so this type itself stays a plain,
/// DI-agnostic <see cref="DbContext"/> with a public constructor taking
/// <see cref="DbContextOptions{TContext}"/>.
/// </summary>
public sealed class PostgresNostrRelayDbContext(DbContextOptions<PostgresNostrRelayDbContext> options) : DbContext(options)
{
    public DbSet<NostrEventEntity> Events => Set<NostrEventEntity>();
    public DbSet<EventTagEntity> EventTags => Set<EventTagEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NostrEventEntity>(entity =>
        {
            // Belt-and-suspenders, not the primary enforcement mechanism: PostgresEventStore
            // already serializes replaceable/addressable writes per key via
            // pg_advisory_xact_lock before ever reaching these. These exist so that if
            // application logic ever had a bug, the database itself would refuse to end up
            // with two rows for the same key, rather than silently allowing it.
            entity.HasIndex(e => new { e.Pubkey, e.Kind })
                .HasDatabaseName("uq_events_replaceable")
                .IsUnique()
                .HasFilter("kind = 0 OR kind = 3 OR (kind >= 10000 AND kind < 20000)");

            entity.HasIndex(e => new { e.Pubkey, e.Kind, e.DTag })
                .HasDatabaseName("uq_events_addressable")
                .IsUnique()
                .HasFilter("kind >= 30000 AND kind < 40000");
        });
    }
}