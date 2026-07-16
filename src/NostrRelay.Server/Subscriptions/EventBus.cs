using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NostrRelay.Core;
using NostrRelay.Server.Configuration;

namespace NostrRelay.Server.Subscriptions;

/// <summary>
/// The central event bus (Section 5.3): every successfully-stored (or ephemeral) event
/// flows through here exactly once, decoupling ingestion from subscription matching and
/// per-connection delivery. <see cref="EventFanOutService"/> is the sole reader.
///
/// Bounded with <see cref="BoundedChannelFullMode.Wait"/> rather than a drop policy: Section
/// 4.2 requires no event loss on the ingestion path, so if the bus is ever full (meaning
/// the fan-out consumer has fallen behind), publishers block rather than silently drop an
/// event. This is a different tradeoff than the per-connection outbound channels
/// (<see cref="ConnectionRegistry"/>'s targets), where dropping to one slow subscriber is
/// an acceptable, isolated cost, dropping here would lose an event for every subscriber.
/// </summary>
public sealed class EventBus(IOptions<RelayLimitsOptions> limitsOptions)
{
    private readonly Channel<NostrEvent> _channel = Channel.CreateBounded<NostrEvent>(new BoundedChannelOptions(limitsOptions.Value.EventBusCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    public ValueTask PublishAsync(NostrEvent evt, CancellationToken ct) => _channel.Writer.WriteAsync(evt, ct);

    public IAsyncEnumerable<NostrEvent> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}