using NostrRelay.Core;

namespace NostrRelay.Storage.Ef;

/// <summary>
/// The single seam between <see cref="NostrEvent"/> (Core's domain type) and the EF
/// persistence shapes. Provider-agnostic and defined once, rather than copied per provider:
/// nothing in here is engine-specific, and the JSON handling that used to live here is an EF
/// value conversion (<see cref="NostrEventTagsConversion"/>).
///
/// Note what is deliberately absent: the Postgres copy of this mapper used to call
/// <c>TrimEnd()</c> on <c>Id</c>, <c>Pubkey</c>, and <c>Sig</c> to strip the blank padding
/// that <c>char(n)</c> columns return on read. That trimming still happens, but as a value
/// conversion declared next to the <c>char(n)</c> column type in
/// <c>PostgresNostrEventEntityConfiguration</c>, which is where the behavior originates. Keep
/// it that way: the moment this mapper starts compensating for one engine's column types, it
/// stops being shareable and the two copies grow back.
/// </summary>
public static class NostrEventEntityMapper
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
            Tags = evt.Tags,
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
        Tags = entity.Tags,
        Content = entity.Content,
        Sig = entity.Sig,
    };

    /// <summary>
    /// Builds the normalized tag rows for an event: only single ASCII-letter tag names are
    /// indexed, per NIP-01's single-letter filter convention.
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