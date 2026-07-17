namespace NostrRelay.Server.Configuration;

/// <summary>Bound from the "ExpirationSweep" configuration section. Controls how often
/// <see cref="Expiration.ExpirationSweepService"/> calls <c>IEventStore.DeleteExpiredEventsAsync</c>.
/// This is purely a storage-reclamation cadence, NIP-40's actual correctness guarantee
/// (never serving an expired event) doesn't depend on this interval at all, that's
/// enforced unconditionally at query time regardless of how often the sweep runs.</summary>
public sealed class ExpirationSweepOptions
{
    public int IntervalSeconds { get; set; } = 300;
}
