using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// EF Core mapping for the <c>event_tags</c> table (Section 5.2's normalized single-letter
/// tag index). The original hand-written schema had no primary key at all, fine for
/// Dapper's insert-only usage, but EF's change tracker requires every entity type it can
/// <c>Add()</c> to have one. <see cref="Id"/> is a new surrogate identity column added for
/// that reason; it carries no domain meaning, tag rows are still looked up by
/// (event_id, tag_name, tag_value) exactly as before, this is purely what makes the row
/// addressable to EF.
///
/// The cascade-delete behavior on the FK to <see cref="NostrEventEntity"/> needs no
/// attribute: EF's default convention is to cascade for a required (non-nullable) foreign
/// key, which <see cref="EventId"/> is, matching the original
/// <c>REFERENCES events(id) ON DELETE CASCADE</c>.
/// </summary>
[Table("event_tags")]
[Index(nameof(TagName), nameof(TagValue), Name = "idx_event_tags_name_value")]
[Index(nameof(EventId), Name = "idx_event_tags_event_id")]
public sealed class EventTagEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("event_id", TypeName = "char(64)")]
    public string EventId { get; set; } = "";

    [Required]
    [Column("tag_name")]
    public string TagName { get; set; } = "";

    [Required]
    [Column("tag_value")]
    public string TagValue { get; set; } = "";

    [ForeignKey(nameof(EventId))]
    public NostrEventEntity? Event { get; set; }
}