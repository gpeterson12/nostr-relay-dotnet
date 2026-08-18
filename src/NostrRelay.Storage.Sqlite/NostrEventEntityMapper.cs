using System.Text.Json;
using NostrRelay.Core;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// The single seam between <see cref="NostrEvent"/> (Core's domain type) and
/// <see cref="NostrEventEntity"/>/<see cref="EventTagEntity"/> (EF's persistence shapes).
/// Structurally identical to the Postgres project's mapper, kept as a separate copy per
/// project rather than a shared abstraction, same allowance as the filter query builders
/// (Section 3.4: "shared or parallel-but-tested component").
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
        Id = entity.Id,
        Pubkey = entity.Pubkey,
        CreatedAt = entity.CreatedAt,
        Kind = entity.Kind,
        Tags = JsonSerializer.Deserialize<List<List<string>>>(entity.TagsJson) ?? [],
        Content = entity.Content,
        Sig = entity.Sig,
    };

    /// <summary>
    /// Builds the normalized tag rows for an event: only single ASCII-letter tag names are
    /// indexed, matching the original InsertTagsAsync filter.
    /// <see cref="EventTagEntity.Id"/> is left unset; the database assigns it on insert.
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
