using Microsoft.EntityFrameworkCore;
using NostrRelay.Storage.Ef;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// EF Core context for the Postgres event store. Entity sets live on
/// <see cref="NostrRelayDbContext"/>; all mapping, comes from the <c>IEntityTypeConfiguration</c>
/// implementations in this assembly. A provider-specific context type is still required
/// because EF keys migration history to a concrete context.
/// </summary>
public sealed class PostgresNostrRelayDbContext(DbContextOptions<PostgresNostrRelayDbContext> options)
    : NostrRelayDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PostgresNostrRelayDbContext).Assembly);
}
