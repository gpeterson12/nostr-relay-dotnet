using Microsoft.EntityFrameworkCore;
using NostrRelay.Storage.Ef;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// EF Core context for the SQLite event store. Entity sets live on
/// <see cref="NostrRelayDbContext"/>; all mapping comes from the
/// <c>IEntityTypeConfiguration</c> implementations in this assembly, which extend the
/// shared configuration bases. A provider-specific context type is still required because
/// EF keys migration history to a concrete context.
/// </summary>
public sealed class SqliteNostrRelayDbContext(DbContextOptions<SqliteNostrRelayDbContext> options)
    : NostrRelayDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SqliteNostrRelayDbContext).Assembly);
}
