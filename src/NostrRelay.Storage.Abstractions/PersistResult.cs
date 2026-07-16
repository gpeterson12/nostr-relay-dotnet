namespace NostrRelay.Storage.Abstractions;

/// <summary>
/// What happened to an event on a <see cref="IEventStore.SaveEventAsync"/> call. Deliberately
/// separate from the eventual <c>["OK", ...]</c> wire message: that mapping is a server-layer
/// concern (Section 6), this type only reports storage-layer fact.
///
/// Recommended (not enforced) mapping to OK's accepted flag, per NIP-01's standardized
/// prefixes:
/// <list type="bullet">
/// <item><see cref="Stored"/> -> <c>OK true, ""</c></item>
/// <item><see cref="Duplicate"/> -> <c>OK true, "duplicate: already have this event"</c>
/// (NIP-01 explicitly shows duplicate as accepted, not rejected)</item>
/// <item><see cref="Superseded"/> -> <c>OK true, ""</c> (the event was valid and understood;
/// it just isn't the copy the relay chooses to keep for a replaceable/addressable key,
/// which is a storage implementation detail, not something to reject with <c>false</c>)</item>
/// <item><see cref="Ephemeral"/> -> <c>OK true, ""</c> (broadcast happens at the server/bus
/// layer, not here, since this type only reports what SaveEventAsync itself did)</item>
/// </list>
/// </summary>
public enum PersistOutcome
{
    /// <summary>Newly persisted: a regular event, the first event for a replaceable/addressable
    /// key, or a replaceable/addressable event that superseded an older stored version.</summary>
    Stored,

    /// <summary>An event with this exact id already exists in storage.</summary>
    Duplicate,

    /// <summary>A replaceable/addressable event already exists for this key with a newer or
    /// equal <c>created_at</c>; per NIP-01, ties are broken by lowest id (lexical order), and
    /// the incoming event was discarded rather than stored.</summary>
    Superseded,

    /// <summary>Ephemeral kind: never touches storage. Reported so the caller knows not to
    /// expect a row, not because SaveEventAsync did any persistence work.</summary>
    Ephemeral,
}

public sealed record PersistResult(PersistOutcome Outcome)
{
    public static PersistResult Stored() => new(PersistOutcome.Stored);
    public static PersistResult Duplicate() => new(PersistOutcome.Duplicate);
    public static PersistResult Superseded() => new(PersistOutcome.Superseded);
    public static PersistResult Ephemeral() => new(PersistOutcome.Ephemeral);
}
