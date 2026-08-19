using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NostrRelay.Storage.Ef;
using NostrRelay.Storage.Ef.Configuration;

namespace NostrRelay.Storage.Sqlite.Configuration;

/// <summary>
/// SQLite's slice of the <c>events</c> mapping. Short by design: SQLite is dynamically typed,
/// so no column type needs declaring (EF's string-to-TEXT and long-to-INTEGER conventions
/// already produce exactly the schema the original hand-written SQL declared), and the
/// addressable coordinate gets a plain composite index rather than the partial unique index
/// Postgres uses.
///
/// The index carries an explicit model name for consistency with the shared base, so that a
/// future index over these same columns is a new index rather than a silent redefinition of
/// this one.
/// </summary>
public sealed class SqliteNostrEventEntityConfiguration : NostrEventEntityConfigurationBase
{
    protected override void ConfigureProvider(EntityTypeBuilder<NostrEventEntity> builder) =>
        builder.HasIndex(e => new { e.Pubkey, e.Kind, e.DTag }, "idx_events_pubkey_kind_dtag");
}