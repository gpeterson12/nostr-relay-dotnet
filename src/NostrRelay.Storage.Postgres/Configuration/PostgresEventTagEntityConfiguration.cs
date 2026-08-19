using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NostrRelay.Storage.Ef;
using NostrRelay.Storage.Ef.Configuration;

namespace NostrRelay.Storage.Postgres.Configuration;

/// <summary>Postgres stores the foreign key as the same fixed-width hex type as the primary
/// key it references, and therefore needs the same blank-padding trim on read. Nothing
/// currently reads <see cref="EventTagEntity.EventId"/> back into a domain object (tag
/// filtering only ever tests it inside a subquery, where SQL's own blank-insensitive
/// comparison applies), so this is here for consistency with the column it mirrors rather
/// than to fix an observed bug.</summary>
public sealed class PostgresEventTagEntityConfiguration : EventTagEntityConfigurationBase
{
    private static readonly ValueConverter<string, string> TrimBlankPadding =
        new(value => value, value => value.TrimEnd());

    protected override void ConfigureProvider(EntityTypeBuilder<EventTagEntity> builder) =>
        builder.Property(t => t.EventId).HasColumnType("char(64)").HasConversion(TrimBlankPadding);
}