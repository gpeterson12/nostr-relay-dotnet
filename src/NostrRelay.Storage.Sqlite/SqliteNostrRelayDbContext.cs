using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// EF Core context for the SQLite event store. Unlike the Postgres side, every index in
/// this schema (see 003_create_indexes.sql) is a plain, unfiltered index, no partial
/// uniques, no GIN, so there's nothing left over that data annotations on
/// <see cref="NostrEventEntity"/>/<see cref="EventTagEntity"/> can't express, and no
/// <c>OnModelCreating</c> override is needed at all.
///
/// One context per operation (Section 5.2's "connection per operation" carried over to EF):
/// <see cref="SqliteEventStore"/> creates and disposes an instance per call via a
/// <see cref="PooledDbContextFactory{TContext}"/>. This type itself stays a plain,
/// DI-agnostic <see cref="DbContext"/> with a public constructor taking
/// <see cref="DbContextOptions{TContext}"/>.
/// </summary>
public sealed class SqliteNostrRelayDbContext(DbContextOptions<SqliteNostrRelayDbContext> options) : DbContext(options)
{
    public DbSet<NostrEventEntity> Events => Set<NostrEventEntity>();
    public DbSet<EventTagEntity> EventTags => Set<EventTagEntity>();
}
