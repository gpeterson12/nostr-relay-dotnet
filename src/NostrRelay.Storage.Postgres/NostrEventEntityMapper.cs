using System.Text.Json;
using NostrRelay.Core;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// The single seam between <see cref="NostrEvent"/> (Core's domain type) and
/// <see cref="NostrEventEntity"/>/<see cref="EventTagEntity"/> (EF's persistence shapes).
/// Replaces both the old <c>EventRow.ToNostrEvent()</c> and
/// <c>PostgresEventStore.BuildInsertParameters</c>.
/// </summary>
internal static class NostrEventEntityMapper
{
    public static NostrEventEntity ToEntity(this NostrEvent evt)
    {
        NostrEventKindCategory category = evt.Classify();

        return new NostrEventEntity
        {
            Id = evt.Id,
            Pubkey = evt.Pubkey,
            CreatedAt = evt.CreatedAt,
            Kind = evt.Kind,
            TagsJson = JsonSerializer.Serialize(evt.Tags),
            Content = evt.Content,
            Sig = evt.Sig,
            ExpiresAt = ExtractExpiresAt(evt),
            DTag = category == NostrEventKindCategory.Addressable ? evt.GetFirstTagValue("d") ?? "" : null,
        };
    }

    public static NostrEvent ToDomain(this NostrEventEntity entity) => new()
    {
        // Id/Pubkey/Sig are char(64)/char(128) columns; Postgres space-pads short values
        // to the declared length and returns that padding on read (unlike varchar). Real
        // Nostr ids/pubkeys/sigs are always exact-length hex, so this is defensive in
        // practice, but test fixtures and any other short placeholder values would
        // otherwise come back with trailing spaces baked into equality comparisons.
        Id = entity.Id.TrimEnd(),
        Pubkey = entity.Pubkey.TrimEnd(),
        CreatedAt = entity.CreatedAt,
        Kind = entity.Kind,
        Tags = JsonSerializer.Deserialize<List<List<string>>>(entity.TagsJson) ?? [],
        Content = entity.Content,
        Sig = entity.Sig.TrimEnd(),
    };

    /// <summary>
    /// Builds the normalized tag rows for an event (Section 5.2's event_tags table): only
    /// single ASCII-letter tag names are indexed, matching the original InsertTagsAsync
    /// filter. <see cref="EventTagEntity.Id"/> is left unset; EF/Postgres assigns it on
    /// insert.
    /// </summary>
    public static List<EventTagEntity> ToTagEntities(this NostrEvent evt) =>
        evt.Tags
            .Where(tag => tag.Count >= 2 && tag[0].Length == 1 && char.IsAsciiLetter(tag[0][0]))
            .Select(tag => new EventTagEntity { EventId = evt.Id, TagName = tag[0], TagValue = tag[1] })
            .ToList();

    private static long? ExtractExpiresAt(NostrEvent evt)
    {
        var raw = evt.GetFirstTagValue("expiration");
        return raw is not null && long.TryParse(raw, out var value) ? value : null;
    }
}