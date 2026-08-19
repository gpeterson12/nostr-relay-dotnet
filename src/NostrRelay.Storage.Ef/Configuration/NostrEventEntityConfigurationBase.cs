using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NostrRelay.Storage.Ef.Configuration;

/// <summary>
/// Everything about the <c>events</c> mapping that is true regardless of engine: table and
/// column names, the key, the <see cref="NostrEventEntity.Tags"/> value conversion, and the
/// indexes that exist identically on both providers.
///
/// Genuinely provider-specific mapping goes in <see cref="ConfigureProvider"/>, which each
/// provider's concrete configuration overrides. That is the whole point of the split: the
/// differences that remain are the ones that are real (Postgres fixed-width hex columns and
/// jsonb, its partial unique indexes, SQLite's plain addressable index), and they are
/// visible in one short method each rather than buried in two full copies of the entity.
///
/// Every index below is declared through the two-argument <c>HasIndex</c> overload, which
/// takes an explicit model name. This is not cosmetic. The single-argument overload
/// identifies an index by its property set, so a provider hook calling <c>HasIndex</c> on
/// the same columns would fetch this index builder back and mutate it in place rather than
/// declaring a second one. That is exactly how the plain (pubkey, kind) index below can
/// silently become Postgres's partial unique index over the same columns. Naming each index
/// keeps them distinct entities in the model, and the database name defaults to the model
/// name, so no separate <c>HasDatabaseName</c> call is needed.
///
/// Abstract on purpose: <c>ApplyConfigurationsFromAssembly</c> skips abstract types, so this
/// is never picked up directly even though it lives in an assembly both providers reference.
/// </summary>
public abstract class NostrEventEntityConfigurationBase : IEntityTypeConfiguration<NostrEventEntity>
{
    public void Configure(EntityTypeBuilder<NostrEventEntity> builder)
    {
        builder.ToTable("events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Pubkey).HasColumnName("pubkey").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.Kind).HasColumnName("kind");
        builder.Property(e => e.Content).HasColumnName("content").IsRequired();
        builder.Property(e => e.Sig).HasColumnName("sig").IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.DTag).HasColumnName("d_tag");

        builder.Property(e => e.Tags)
            .HasColumnName("tags")
            .HasConversion(NostrEventTagsConversion.Converter, NostrEventTagsConversion.Comparer)
            .IsRequired();

        // Query-path indexes, backing the filter shapes NIP-01 REQ messages actually produce:
        // author plus kind, kind plus time window, bare time window, and the NIP-40 sweep.
        //
        // idx_events_pubkey_kind is deliberately separate from Postgres's partial unique index
        // over the same two columns: that one only covers replaceable kinds, so it cannot
        // serve an authors+kinds filter for kind 1, which is the common case.
        builder.HasIndex(e => new { e.Pubkey, e.Kind }, "idx_events_pubkey_kind");
        builder.HasIndex(e => new { e.Kind, e.CreatedAt }, "idx_events_kind_created_at");
        builder.HasIndex(e => e.CreatedAt, "idx_events_created_at");
        builder.HasIndex(e => e.ExpiresAt, "idx_events_expires_at");

        ConfigureProvider(builder);
    }

    /// <summary>Provider-specific mapping: column types with no cross-engine meaning, and
    /// indexes only one engine can express.</summary>
    protected abstract void ConfigureProvider(EntityTypeBuilder<NostrEventEntity> builder);
}