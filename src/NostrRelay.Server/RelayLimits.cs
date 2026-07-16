namespace NostrRelay.Server;

/// <summary>
/// The one relay-wide constant that stayed a constant through Milestone 8's policy layer:
/// everything else previously here (<c>MaxMessageBytes</c>, <c>MaxSubscriptionsPerConnection</c>)
/// moved to <see cref="Configuration.RelayLimitsOptions"/>, bound from the "Limits"
/// configuration section, so operators can tune them without a rebuild. This one stays a
/// constant because it isn't part of the spec's "Limits" config shape (Section 5.6) and
/// isn't itself a policy/abuse-resistance knob, it's a storage-query default.
/// </summary>
public static class RelayLimits
{
    /// <summary>The default historical-query limit applied when a filter omits its own
    /// <c>limit</c> (see SqliteEventStore/PostgresEventStore's QueryAsync).</summary>
    public const int DefaultQueryLimit = 500;
}