using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// EF Core mapping for the <c>events</c> table. Deliberately a plain persistence-shape
/// class, not <c>NostrRelay.Core.NostrEvent</c> itself: Core's record stays free of any EF
/// mapping concerns, and <see cref="NostrEventEntityMapper"/> is the single seam that
/// converts between the two.
///
/// Table/column names, types, and the plain named indexes below are all declared via data
/// annotations. The two Postgres-specific indexes that don't have an attribute equivalent
/// (the partial unique indexes) are configured in <see cref="PostgresNostrRelayDbContext.OnModelCreating"/>
/// instead, see that file's comment. There is deliberately no plain (pubkey, kind, d_tag)
/// index here: <c>d_tag</c> is null for every non-addressable row, so a plain index on
/// this triple would mostly index nulls while duplicating what the partial unique index
/// on the same columns already covers for the addressable rows that actually use it.
///
/// <see cref="TagsJson"/> stays a plain string, serialized/deserialized explicitly via
/// <see cref="System.Text.Json.JsonSerializer"/> in the mapper, rather than relying on
/// Npgsql's EF provider doing anything clever with the jsonb column. No index is declared
/// on it: all tag filtering goes through the normalized <c>event_tags</c> table, so a GIN
/// index here would only add write-path maintenance cost for a JSONB containment query
/// path nothing currently uses.
/// </summary>
[Table("events")]
[Index(nameof(Pubkey), nameof(Kind), Name = "idx_events_pubkey_kind")]
[Index(nameof(Kind), nameof(CreatedAt), Name = "idx_events_kind_created_at")]
[Index(nameof(CreatedAt), Name = "idx_events_created_at")]
[Index(nameof(ExpiresAt), Name = "idx_events_expires_at")]
public sealed class NostrEventEntity
{
    [Key]
    [Column("id", TypeName = "char(64)")]
    public string Id { get; set; } = "";

    [Required]
    [Column("pubkey", TypeName = "char(64)")]
    public string Pubkey { get; set; } = "";

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("kind")]
    public int Kind { get; set; }

    [Required]
    [Column("tags", TypeName = "jsonb")]
    public string TagsJson { get; set; } = "[]";

    [Required]
    [Column("content")]
    public string Content { get; set; } = "";

    [Required]
    [Column("sig", TypeName = "char(128)")]
    public string Sig { get; set; } = "";

    [Column("expires_at")]
    public long? ExpiresAt { get; set; }

    [Column("d_tag")]
    public string? DTag { get; set; }

    public ICollection<EventTagEntity> EventTags { get; set; } = new List<EventTagEntity>();
}