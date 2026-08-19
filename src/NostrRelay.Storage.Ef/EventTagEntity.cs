namespace NostrRelay.Storage.Ef;

/// <summary>
/// EF Core persistence shape for the normalized <c>event_tags</c> table, shared by every
/// provider. Tag filtering (<c>#e</c>, <c>#p</c>, and friends) queries these rows rather
/// than the JSON blob on <see cref="NostrEventEntity.Tags"/>, so a single-letter tag lookup
/// is an index seek instead of a document scan.
///
/// <see cref="Id"/> is a surrogate identity column with no domain meaning. It exists purely
/// because EF's change tracker needs a key to <c>Add()</c> a row; the natural key here
/// (event id, tag name, tag value) is not unique, since one event may legitimately carry
/// the same tag twice.
///
/// Cascade delete needs no explicit call: EF's convention cascades for a required
/// (non-nullable) foreign key, which <see cref="EventId"/> is. On SQLite that cascade only
/// fires if foreign key enforcement is on for the connection, which the app's connection
/// string sets via <c>ForeignKeys=true</c>.
/// </summary>
public sealed class EventTagEntity
{
    public long Id { get; set; }

    public string EventId { get; set; } = "";

    public string TagName { get; set; } = "";

    public string TagValue { get; set; } = "";

    public NostrEventEntity? Event { get; set; }
}
