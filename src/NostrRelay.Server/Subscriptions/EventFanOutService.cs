using NostrRelay.Core;
using NostrRelay.Core.Protocol;

namespace NostrRelay.Server.Subscriptions;

/// <summary>
/// The bus's sole consumer (Section 5.3 steps 3-4): reads every published event, matches
/// it against <see cref="SubscriptionRegistry"/>, and enqueues an <see cref="EventRelayMessage"/>
/// onto each matching connection's outbound channel via <see cref="ConnectionRegistry"/>.
///
/// Runs sequentially over matching subscriptions rather than via <c>Parallel.ForEachAsync</c>;
/// the spec notes parallel dispatch as something to reach for "where beneficial... for very
/// large subscriber counts" (Section 5.3), which is a benchmark-driven optimization, not a
/// correctness requirement, so it's deferred until Section 4.1's fan-out latency targets are
/// actually measured against this simpler version.
/// </summary>
public sealed class EventFanOutService(
    EventBus bus,
    SubscriptionRegistry subscriptions,
    ConnectionRegistry connections,
    ILogger<EventFanOutService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (NostrEvent evt in bus.ReadAllAsync(stoppingToken))
        {
            foreach (var (connectionId, subscriptionId) in subscriptions.FindMatching(evt))
            {
                if (!connections.TryGetWriter(connectionId, out var writer))
                    continue; // connection disconnected between matching and delivery; drop silently

                var message = new EventRelayMessage(subscriptionId, evt);
                if (!writer.TryWrite(message))
                {
                    logger.LogWarning(
                        "dropped live event {EventId} for connection {ConnectionId} subscription {SubscriptionId}: outbound channel full",
                        evt.Id, connectionId, subscriptionId);
                }
            }
        }
    }
}
