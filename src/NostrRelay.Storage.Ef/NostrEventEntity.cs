namespace NostrRelay.Storage.Ef;

/// <summary>
/// EF Core persistence shape for the <c>events</c> table, shared by every provider.
/// Deliberately not <c>NostrRelay.Core.NostrEvent</c> itself: Core's record stays free of
/// any persistence concern, and <see cref="NostrEventEntityMapper"/> is the single seam
/// that converts between the two.
///
/// This type carries no mapping attributes at all. Everything about how it maps (table and
/// column names, keys, indexes, the <see cref="Tags"/> value conversion) lives in
/// <see cref="Configuration.NostrEventEntityConfigurationBase"/>, with the parts that
/// genuinely differ between engines supplied by each provider's own subclass. That split is
/// what lets one entity definition serve both SQLite and Postgres instead of two
/// near-identical copies drifting apart.
///
/// <see cref="Tags"/> is the domain-shaped nested list rather than a pre-serialized JSON
/// string: the JSON round-trip is an EF value conversion (see
/// <see cref="NostrEventTagsConversion"/>), so serialization is part of the model rather
/// than something the mapper has to remember to do on both sides.
/// </summary>
public sealed class NostrEventEntity
{
    public string Id { get; set; } = "";

    public string Pubkey { get; set; } = "";

    public long CreatedAt { get; set; }

    public int Kind { get; set; }

    public IReadOnlyList<IReadOnlyList<string>> Tags { get; set; } = [];

    public string Content { get; set; } = "";

    public string Sig { get; set; } = "";

    /// <summary>NIP-40 expiration, extracted from the <c>expiration</c> tag at write time so
    /// it can be indexed and filtered in SQL. Null when the event never expires.</summary>
    public long? ExpiresAt { get; set; }

    /// <summary>The <c>d</c> tag value for addressable events (kinds 30000-39999), null for
    /// every other kind. Denormalized out of <see cref="Tags"/> so the addressable
    /// (pubkey, kind, d) coordinate is a plain indexable column.</summary>
    public string? DTag { get; set; }

    public ICollection<EventTagEntity> EventTags { get; set; } = new List<EventTagEntity>();
}
