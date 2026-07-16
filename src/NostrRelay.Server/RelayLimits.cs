namespace NostrRelay.Server;

/// <summary>
/// Centralized limit constants, referenced both by enforcement code
/// (<see cref="WebSockets.NostrConnectionHandler"/>, <see cref="Subscriptions.SubscriptionRegistry"/>)
/// and by the NIP-11 relay info document (<see cref="Info.RelayInfoDocumentFactory"/>), so
/// what's advertised in the "limitation" object is always exactly what's enforced, never a
/// second hardcoded guess that can silently drift out of sync. These become real
/// configuration values in Milestone 8's policy layer; kept as constants here until then.
/// </summary>
public static class RelayLimits
{
    /// <summary>Section 4.3: raw WebSocket message size cap. Matches NIP-11's
    /// <c>max_message_length</c> exactly, by definition, not by coincidence.</summary>
    public const int MaxMessageBytes = 65536;

    /// <summary>Section 3.5: default max subscriptions per connection.</summary>
    public const int MaxSubscriptionsPerConnection = 20;

    /// <summary>The default historical-query limit applied when a filter omits its own
    /// <c>limit</c> (see SqliteEventStore/PostgresEventStore's QueryAsync).</summary>
    public const int DefaultQueryLimit = 500;
}
