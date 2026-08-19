using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NostrRelay.Storage.Ef;
using NostrRelay.Storage.Ef.Configuration;

namespace NostrRelay.Storage.Sqlite.Configuration;

/// <summary>SQLite needs nothing beyond the shared <c>event_tags</c> mapping; the override
/// is empty rather than absent so the provider hook stays explicit and discoverable.</summary>
public sealed class SqliteEventTagEntityConfiguration : EventTagEntityConfigurationBase
{
    protected override void ConfigureProvider(EntityTypeBuilder<EventTagEntity> builder)
    {
        // No SQLite-specific mapping required.
    }
}
