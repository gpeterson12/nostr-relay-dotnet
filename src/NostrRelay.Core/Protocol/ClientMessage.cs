namespace NostrRelay.Core.Protocol;

/// <summary>
/// Base type for the five client-to-relay message shapes (Section 2.2). Produced by
/// <see cref="ClientMessageParser"/>, consumed by the server layer's connection loop.
/// A closed hierarchy (all cases known and exhaustively switchable) rather than an open
/// one, matching the fixed, spec-defined set of inbound message types.
/// </summary>
public abstract record ClientMessage;

/// <summary><c>["EVENT", &lt;event&gt;]</c> — publish an event.</summary>
public sealed record EventClientMessage(NostrEvent Event) : ClientMessage;

/// <summary><c>["REQ", &lt;subscription_id&gt;, &lt;filter1&gt;, &lt;filter2&gt;, ...]</c> — open/replace a subscription. Filters are OR'd.</summary>
public sealed record ReqClientMessage(string SubscriptionId, IReadOnlyList<NostrFilter> Filters) : ClientMessage;

/// <summary><c>["CLOSE", &lt;subscription_id&gt;]</c> — close a subscription.</summary>
public sealed record CloseClientMessage(string SubscriptionId) : ClientMessage;

/// <summary><c>["AUTH", &lt;event&gt;]</c> — respond to a relay-issued auth challenge (NIP-42).</summary>
public sealed record AuthClientMessage(NostrEvent Event) : ClientMessage;

/// <summary><c>["COUNT", &lt;subscription_id&gt;, &lt;filter&gt;]</c> — request a count instead of full events (NIP-45).</summary>
public sealed record CountClientMessage(string SubscriptionId, NostrFilter Filter) : ClientMessage;
