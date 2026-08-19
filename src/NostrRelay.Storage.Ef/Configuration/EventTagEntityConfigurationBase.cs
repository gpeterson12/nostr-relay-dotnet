using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NostrRelay.Storage.Ef.Configuration;

/// <summary>
/// Shared mapping for the normalized <c>event_tags</c> table. Only the fixed-width hex
/// column type on <c>event_id</c> differs between providers, so the provider hook here is
/// smaller than the events one.
///
/// Indexes are declared with explicit model names for the same reason as
/// <see cref="NostrEventEntityConfigurationBase"/>: so a provider hook cannot accidentally
/// redefine one of them by declaring an index over the same columns.
/// </summary>
public abstract class EventTagEntityConfigurationBase : IEntityTypeConfiguration<EventTagEntity>
{
    public void Configure(EntityTypeBuilder<EventTagEntity> builder)
    {
        builder.ToTable("event_tags");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(t => t.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(t => t.TagName).HasColumnName("tag_name").IsRequired();
        builder.Property(t => t.TagValue).HasColumnName("tag_value").IsRequired();

        // The lookup index for "#e"/"#p"-style filters, plus the FK index the cascade delete
        // and per-event tag load both rely on.
        builder.HasIndex(t => new { t.TagName, t.TagValue }, "idx_event_tags_name_value");
        builder.HasIndex(t => t.EventId, "idx_event_tags_event_id");

        builder.HasOne(t => t.Event)
            .WithMany(e => e.EventTags)
            .HasForeignKey(t => t.EventId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        ConfigureProvider(builder);
    }

    protected abstract void ConfigureProvider(EntityTypeBuilder<EventTagEntity> builder);
}