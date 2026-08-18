using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// EF Core mapping for the <c>events</c> table. A plain persistence-shape class, not
/// <c>NostrRelay.Core.NostrEvent</c> itself, <see cref="NostrEventEntityMapper"/> is the
/// single seam that converts between the two.
///
/// No explicit <c>HasColumnType</c>/length annotations here, unlike the Postgres entity:
/// SQLite is dynamically typed and the original hand-written schema (001_create_events_table.sql)
/// only ever declared bare <c>TEXT</c>/<c>INTEGER</c>, no length constraints, EF's own
/// string-to-TEXT/long-to-INTEGER convention already produces exactly that.
///
/// All indexes from the original 003_create_indexes.sql are plain (unfiltered), so unlike
/// the Postgres side, nothing here needs an OnModelCreating override, every index is
/// expressible as an attribute (see <see cref="NostrRelayDbContext"/>'s comment).
/// </summary>
[Table("events")]
[Index(nameof(Pubkey), nameof(Kind), Name = "idx_events_pubkey_kind")]
[Index(nameof(Kind), nameof(CreatedAt), Name = "idx_events_kind_created_at")]
[Index(nameof(CreatedAt), Name = "idx_events_created_at")]
[Index(nameof(Pubkey), nameof(Kind), nameof(DTag), Name = "idx_events_pubkey_kind_dtag")]
[Index(nameof(ExpiresAt), Name = "idx_events_expires_at")]
public sealed class NostrEventEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = "";

    [Required]
    [Column("pubkey")]
    public string Pubkey { get; set; } = "";

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("kind")]
    public int Kind { get; set; }

    [Required]
    [Column("tags")]
    public string TagsJson { get; set; } = "[]";

    [Required]
    [Column("content")]
    public string Content { get; set; } = "";

    [Required]
    [Column("sig")]
    public string Sig { get; set; } = "";

    [Column("expires_at")]
    public long? ExpiresAt { get; set; }

    [Column("d_tag")]
    public string? DTag { get; set; }

    public ICollection<EventTagEntity> EventTags { get; set; } = new List<EventTagEntity>();
}
