using System.Collections.Concurrent;
using NostrRelay.Core;

namespace NostrRelay.Server.Subscriptions;

/// <summary>
/// Thread-safe registry of every currently-active subscription across every connection
/// (Section 5.3): <c>ConnectionId -> SubscriptionId -> Filters</c>. This is purely
/// bookkeeping for filter matching; it holds no reference to sockets or channels, that's
/// <see cref="ConnectionRegistry"/>'s job, kept separate so fan-out matching logic never
/// needs to know anything about WebSockets.
/// </summary>
public sealed class SubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IReadOnlyList<NostrFilter>>> _subscriptions = new();

    /// <summary>
    /// Registers a subscription, or replaces it if <paramref name="subscriptionId"/> is
    /// already in use on this connection (Section 3.5: "A REQ reusing an existing
    /// subscription id on the same connection replaces that subscription"). Returns false
    /// if this would exceed the per-connection cap and the subscription was not
    /// registered, replacing an existing id never counts against the cap.
    /// </summary>
    public bool TryAddOrReplace(string connectionId, string subscriptionId, IReadOnlyList<NostrFilter> filters)
    {
        var connectionSubs = _subscriptions.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, IReadOnlyList<NostrFilter>>());

        if (!connectionSubs.ContainsKey(subscriptionId) && connectionSubs.Count >= RelayLimits.MaxSubscriptionsPerConnection)
            return false;

        connectionSubs[subscriptionId] = filters;
        return true;
    }

    public void Remove(string connectionId, string subscriptionId)
    {
        if (_subscriptions.TryGetValue(connectionId, out var connectionSubs))
            connectionSubs.TryRemove(subscriptionId, out _);
    }

    /// <summary>Called on disconnect (Section 3.5: "all subscriptions for that connection
    /// are cleaned up immediately, no leaks").</summary>
    public void RemoveConnection(string connectionId) => _subscriptions.TryRemove(connectionId, out _);

    /// <summary>
    /// Every (connectionId, subscriptionId) pair whose filters match <paramref name="evt"/>.
    /// Called once per published event from <see cref="EventFanOutService"/>; O(active
    /// subscriptions) per call, per Section 5.3's stated performance goal.
    /// </summary>
    public IEnumerable<(string ConnectionId, string SubscriptionId)> FindMatching(NostrEvent evt)
    {
        foreach (var (connectionId, subs) in _subscriptions)
        {
            foreach (var (subscriptionId, filters) in subs)
            {
                if (filters.Any(f => f.Matches(evt)))
                    yield return (connectionId, subscriptionId);
            }
        }
    }

    /// <summary>Total active subscriptions across every connection. Backs the
    /// <c>nostr_relay_subscriptions_active</c> metrics gauge.</summary>
    public int TotalSubscriptionCount => _subscriptions.Values.Sum(subs => subs.Count);
}