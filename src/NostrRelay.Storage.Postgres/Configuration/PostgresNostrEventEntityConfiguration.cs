using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NostrRelay.Storage.Ef;
using NostrRelay.Storage.Ef.Configuration;

namespace NostrRelay.Storage.Postgres.Configuration;

/// <summary>
/// Postgres's slice of the <c>events</c> mapping: fixed-width hex column types, jsonb for the
/// tags document, and the two partial unique indexes that have no attribute or cross-provider
/// equivalent.
///
/// The partial uniques are belt-and-suspenders, not the primary enforcement mechanism:
/// <c>PostgresEventStore</c> already serializes replaceable and addressable writes per key
/// with <c>pg_advisory_xact_lock</c>. They exist so that an application-logic bug still cannot
/// leave two rows for the same key.
///
/// <c>uq_events_replaceable</c> covers the same two columns as the shared
/// <c>idx_events_pubkey_kind</c>, and both are needed. The unique one is partial, restricted
/// to replaceable kinds, so the planner can only use it for queries that imply that
/// predicate; an <c>authors</c> plus <c>kinds</c> filter for kind 1, the common case, still
/// needs the plain index. Note the explicit model-name argument on every <c>HasIndex</c> call
/// here: without it, EF would resolve these back to the base class's index over the same
/// property set and rewrite it in place rather than adding a second index.
///
/// There is deliberately no plain (pubkey, kind, d_tag) index here, unlike SQLite:
/// <c>d_tag</c> is null for every non-addressable row, so a plain index would mostly index
/// nulls while duplicating what the partial unique index already covers.
///
/// No index on <c>tags</c> either: all tag filtering goes through the normalized
/// <c>event_tags</c> table, so a GIN index would add write-path maintenance cost for a
/// containment query path nothing uses.
/// </summary>
public sealed class PostgresNostrEventEntityConfiguration : NostrEventEntityConfigurationBase
{
    /// <summary>
    /// Identity on write, right-trim on read.
    ///
    /// <c>char(n)</c> is blank-padded: Postgres stores a shorter value padded out to the
    /// declared length and hands that padding back on read, unlike <c>varchar</c> or
    /// <c>text</c>. SQL comparisons ignore trailing blanks, so filtering and joining behave
    /// correctly either way, but the .NET string that lands on the entity does not: an id
    /// read back through this column would carry trailing spaces into every downstream
    /// equality check, dictionary lookup, and serialized <c>EVENT</c> frame.
    ///
    /// This is a property of the column type, so it belongs here next to the
    /// <c>HasColumnType("char(64)")</c> that causes it, rather than in the shared mapper,
    /// which has no business knowing which engine it is running against. Real Nostr ids,
    /// pubkeys, and signatures are always exactly 64 or 128 hex characters and are never
    /// padded, so in production this is defensive; it matters for any shorter value, which
    /// is what the contract tests use.
    /// </summary>
    private static readonly ValueConverter<string, string> TrimBlankPadding =
        new(value => value, value => value.TrimEnd());

    protected override void ConfigureProvider(EntityTypeBuilder<NostrEventEntity> builder)
    {
        builder.Property(e => e.Id).HasColumnType("char(64)").HasConversion(TrimBlankPadding);
        builder.Property(e => e.Pubkey).HasColumnType("char(64)").HasConversion(TrimBlankPadding);
        builder.Property(e => e.Sig).HasColumnType("char(128)").HasConversion(TrimBlankPadding);
        builder.Property(e => e.Tags).HasColumnType("jsonb");

        builder.HasIndex(e => new { e.Pubkey, e.Kind }, "uq_events_replaceable")
            .IsUnique()
            .HasFilter("kind = 0 OR kind = 3 OR (kind >= 10000 AND kind < 20000)");

        builder.HasIndex(e => new { e.Pubkey, e.Kind, e.DTag }, "uq_events_addressable")
            .IsUnique()
            .HasFilter("kind >= 30000 AND kind < 40000");
    }
}