namespace NostrRelay.Core.Validation;

/// <summary>
/// NIP-40: "Relays SHOULD drop any events that are published to them if they are
/// expired." Checks the event's "expiration" tag, if present, against the current time;
/// an event whose expiration has already passed is rejected outright at write time
/// rather than accepted and left for the periodic sweep job to clean up.
///
/// Runs after <see cref="PolicyValidator"/> in the production pipeline: this is a
/// NIP-40-specific tag check, not a general identity/kind/timestamp policy concern, kept
/// as its own step so it's easy to find and test independently rather than growing
/// PolicyValidator into a catch-all for every future NIP's write-time rule.
/// </summary>
public sealed class ExpirationValidator : IEventValidator
{
    public ValidationResult Validate(NostrEvent evt)
    {
        var expirationTag = evt.GetFirstTagValue("expiration");
        if (expirationTag is null)
            return ValidationResult.Success();

        if (!long.TryParse(expirationTag, out var expiresAt))
            return ValidationResult.Failure("invalid: expiration tag must be a unix timestamp");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiresAt <= now)
            return ValidationResult.Failure("invalid: event has already expired");

        return ValidationResult.Success();
    }
}
