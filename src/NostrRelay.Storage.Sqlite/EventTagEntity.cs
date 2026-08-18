using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// EF Core mapping for the <c>event_tags</c> table. The original hand-written schema
/// (002_create_event_tags_table.sql) had no primary key, fine for Dapper's insert-only
/// usage, but EF's change tracker requires one to <c>Add()</c> a row. <see cref="Id"/> is a
/// new surrogate identity column added for that reason, same as the Postgres port; it
/// carries no domain meaning.
///
/// The cascade-delete behavior needs no attribute: EF's default convention cascades for a
/// required (non-nullable) foreign key, which <see cref="EventId"/> is, matching the
/// original <c>REFERENCES events(id) ON DELETE CASCADE</c>. That cascade only actually
/// fires at the SQLite engine level if foreign key enforcement is on for the connection,
/// see <see cref="SqliteEventStore"/>'s connection string setup for how that's enabled.
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
    [Column("event_id")]
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
