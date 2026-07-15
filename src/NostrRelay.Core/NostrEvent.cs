namespace NostrRelay.Core;

/// <summary>
/// The atomic unit of data in the Nostr protocol. Immutable by design: an event's
/// id and signature are only valid for the exact field values used to compute them,
/// so any "modification" is really a new event.
/// </summary>
public sealed record NostrEvent
{
    /// <summary>Lowercase hex SHA256 of the canonical serialization. 64 hex chars.</summary>
    public required string Id { get; init; }

    /// <summary>Lowercase hex, 32-byte x-only public key (BIP-340). 64 hex chars.</summary>
    public required string Pubkey { get; init; }

    /// <summary>Unix timestamp in seconds, as claimed by the client. Never trust this server-side.</summary>
    public required long CreatedAt { get; init; }

    public required int Kind { get; init; }

    /// <summary>Array of tag arrays, e.g. [["e", "&lt;event-id&gt;"], ["p", "&lt;pubkey&gt;"]].</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Tags { get; init; }

    public required string Content { get; init; }

    /// <summary>Lowercase hex BIP-340 Schnorr signature over Id. 128 hex chars.</summary>
    public required string Sig { get; init; }

    /// <summary>
    /// Returns the value of the first tag matching <paramref name="tagName"/>, or null if absent.
    /// Used e.g. to pull the "d" tag for addressable events (Section 3.3).
    /// </summary>
    public string? GetFirstTagValue(string tagName)
    {
        foreach (var tag in Tags)
        {
            if (tag.Count >= 2 && tag[0] == tagName)
                return tag[1];
        }

        return null;
    }
}
