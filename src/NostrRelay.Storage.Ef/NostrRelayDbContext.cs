using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Ef;

/// <summary>
/// Provider-agnostic base context. Holds the entity sets that every provider shares; each
/// provider derives a concrete context and applies its own
/// <see cref="Microsoft.EntityFrameworkCore.IEntityTypeConfiguration{TEntity}"/> classes,
/// which in turn extend the shared configuration bases in
/// <c>NostrRelay.Storage.Ef.Configuration</c>.
///
/// Each provider needs its own derived type rather than sharing this one directly: EF's
/// migrations are keyed to a specific context type, and the two providers have separate
/// migration histories.
///
/// One context per operation (Section 5.2's "connection per operation" carried over to EF):
/// the event stores create and dispose an instance per call via a
/// <see cref="PooledDbContextFactory{TContext}"/>, so derived types stay plain and
/// DI-agnostic, taking only <see cref="DbContextOptions"/>.
/// </summary>
public abstract class NostrRelayDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<NostrEventEntity> Events => Set<NostrEventEntity>();

    public DbSet<EventTagEntity> EventTags => Set<EventTagEntity>();
}
