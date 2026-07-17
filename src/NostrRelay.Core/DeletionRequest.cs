namespace NostrRelay.Core;

/// <summary>
/// A parsed NIP-09 kind-5 deletion request: direct event-id references ("e" tags) and
/// addressable-coordinate references ("a" tags, format <c>&lt;kind&gt;:&lt;pubkey&gt;:&lt;d-identifier&gt;</c>).
///
/// Deliberately a two-tier trust model, split across Core and Storage:
/// <list type="bullet">
/// <item><b>"a" tag ownership is validated here, at parse time.</b> The coordinate embeds
/// its own pubkey, so whether it belongs to the deletion request's author is knowable
/// from the tag alone, no storage lookup needed. A coordinate whose pubkey doesn't match
/// the deletion request's own pubkey is silently dropped, exactly as if it were never
/// listed, guarding against someone naming another user's addressable event in a
/// forged-looking "a" tag.</item>
/// <item><b>"e" tag ownership cannot be validated here.</b> An "e" tag is bare event id,
/// its author is only knowable by looking up what's actually stored. That check (and the
/// "a deletion request can't delete another deletion request" rule) happens in
/// <c>IEventStore.DeleteEventsAuthoredByAsync</c>, where the data lives.</item>
/// </list>
/// </summary>
public sealed record DeletionRequest(
    IReadOnlyList<string> EventIds,
    IReadOnlyList<AddressableCoordinate> AddressableCoordinates)
{
    public static DeletionRequest Parse(NostrEvent deletionEvent)
    {
        var eventIds = new List<string>();
        var coordinates = new List<AddressableCoordinate>();

        foreach (var tag in deletionEvent.Tags)
        {
            if (tag.Count < 2)
                continue;

            switch (tag[0])
            {
                case "e":
                    eventIds.Add(tag[1]);
                    break;

                case "a" when AddressableCoordinate.TryParse(tag[1], out AddressableCoordinate coordinate)
                              && coordinate.Pubkey == deletionEvent.Pubkey:
                    coordinates.Add(coordinate);
                    break;
            }
        }

        return new DeletionRequest(eventIds, coordinates);
    }
}

/// <summary>An "a" tag's addressable-event coordinate: <c>&lt;kind&gt;:&lt;pubkey&gt;:&lt;d-identifier&gt;</c>.
/// The d-identifier segment can itself contain colons, so parsing splits into at most
/// three parts rather than on every colon.</summary>
public sealed record AddressableCoordinate(int Kind, string Pubkey, string DTag)
{
    public static bool TryParse(string value, out AddressableCoordinate coordinate)
    {
        var parts = value.Split(':', 3);

        if (parts.Length == 3 && int.TryParse(parts[0], out var kind))
        {
            coordinate = new AddressableCoordinate(kind, parts[1], parts[2]);
            return true;
        }

        coordinate = null!;
        return false;
    }
}
