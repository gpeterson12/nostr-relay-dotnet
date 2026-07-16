namespace NostrRelay.Server.Configuration;

/// <summary>
/// Bound from the "Limits" configuration section (Section 5.6). Defaults here match the
/// spec's illustrative config exactly, except <c>MinProofOfWorkDifficulty</c>: that field
/// is intentionally absent. NIP-13 proof-of-work isn't implemented yet (it's an explicit
/// v1.1 stretch NIP, Milestone 13), and adding a config knob for a check the relay doesn't
/// actually perform would be exactly the kind of unenforced claim the NIP-11 document
/// deliberately avoids elsewhere. It gets added here the moment NIP-13 does.
/// </summary>
public sealed class RelayLimitsOptions
{
    public int MaxConnections { get; set; } = 5000;

    public int MaxSubscriptionsPerConnection { get; set; } = 20;

    public int MaxFiltersPerSubscription { get; set; } = 10;

    /// <summary>Raw WebSocket frame size cap (Section 4.3). Per NIP-11's own description
    /// of <c>max_message_length</c>: "It also effectively limits the maximum size of any
    /// event" — one cap serves both purposes, not two separate numbers.</summary>
    public int MaxEventSizeBytes { get; set; } = 65536;

    /// <summary>Per-connection token bucket capacity/refill rate for EVENT publishes and
    /// REQ subscriptions (Section 4.3: "Rate limit per connection... using a token
    /// bucket").</summary>
    public int EventRateLimitPerMinute { get; set; } = 300;
}
