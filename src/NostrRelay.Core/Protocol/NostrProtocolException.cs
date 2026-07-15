namespace NostrRelay.Core.Protocol;

/// <summary>
/// Thrown when an inbound WebSocket text frame cannot be parsed as a valid Nostr message
/// envelope (Section 2.2): not JSON, not an array, wrong arity for the message type, an
/// unrecognized message type, or a malformed nested event/filter object.
///
/// Deliberately distinct from <see cref="Validation.ValidationResult"/> failures: a
/// protocol exception means "this isn't a message we can even understand" (the server
/// layer should typically respond with NOTICE and may choose to drop the connection for
/// repeated offenses), whereas a failed <see cref="Validation.ValidationResult"/> means
/// "this is a well-formed EVENT that fails business rules" (the server responds with
/// OK false and keeps the connection open).
/// </summary>
public sealed class NostrProtocolException : Exception
{
    public NostrProtocolException(string message) : base(message)
    {
    }

    public NostrProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
