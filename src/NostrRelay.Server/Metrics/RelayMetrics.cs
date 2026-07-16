using System.Collections.Concurrent;

namespace NostrRelay.Server.Metrics;

/// <summary>
/// Cumulative counters for the subset of Section 4.4's observability list that's cheap and
/// natural to capture at this milestone: connection and event counts. Query latency
/// histograms and storage size are deliberately not included yet, they'd need
/// instrumenting every storage call across both providers uniformly, which is better done
/// as its own focused pass (naturally pairs with Milestone 11's benchmarking) than bolted
/// on here.
///
/// Plain <see cref="Interlocked"/> counters rather than <c>System.Diagnostics.Metrics</c>'
/// <c>Counter&lt;T&gt;</c>: that API is built to be observed via a registered listener
/// (e.g. an OpenTelemetry exporter), not queried imperatively for "what's the current
/// value", which is exactly what a hand-rolled /metrics endpoint needs to do on each scrape.
/// </summary>
public sealed class RelayMetrics
{
    private long _connectionsOpenedTotal;
    private long _eventsIngestedTotal;
    private readonly ConcurrentDictionary<string, long> _eventsRejectedByReason = new();

    public void RecordConnectionOpened() => Interlocked.Increment(ref _connectionsOpenedTotal);

    public void RecordEventIngested() => Interlocked.Increment(ref _eventsIngestedTotal);

    /// <summary>Tallied by reason prefix (the part before the first ":" in the OK/NOTICE
    /// message, e.g. "invalid", "error"), matching Section 4.4's "events rejected (by
    /// reason)" and the standardized OK-message prefixes from Section 2.2.</summary>
    public void RecordEventRejected(string reasonPrefix) =>
        _eventsRejectedByReason.AddOrUpdate(reasonPrefix, 1, (_, count) => count + 1);

    public long ConnectionsOpenedTotal => Interlocked.Read(ref _connectionsOpenedTotal);

    public long EventsIngestedTotal => Interlocked.Read(ref _eventsIngestedTotal);

    public IReadOnlyDictionary<string, long> EventsRejectedByReason => _eventsRejectedByReason;
}
