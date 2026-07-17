using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using NostrRelay.Core;
using NostrRelay.Server.Configuration;
using NostrRelay.Server.Subscriptions;

namespace NostrRelay.Benchmarks;

/// <summary>
/// Section 5.3: "Filter matching must be efficient: precompute nothing fancier than a
/// simple predicate evaluation per event, but ensure this is O(active subscriptions) per
/// event... benchmark this explicitly at 10k+ subscriptions." This is that benchmark:
/// <see cref="SubscriptionRegistry.FindMatching"/> against a registry populated with
/// <see cref="SubscriptionCount"/> subscriptions, none of which match the published event,
/// the worst case that forces evaluating every single one.
///
/// Subscriptions are spread across enough distinct connection ids to respect
/// RelayLimitsOptions' default 20-per-connection cap (SubscriptionCount / 20 connections),
/// rather than hitting TryAddOrReplace's cap and silently registering fewer subscriptions
/// than the parameter claims.
/// </summary>
[MemoryDiagnoser]
public class SubscriptionFanOutBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int SubscriptionCount { get; set; }

    private SubscriptionRegistry _registry = null!;
    private NostrEvent _publishedEvent = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new SubscriptionRegistry(Options.Create(new RelayLimitsOptions()));

        const int maxSubscriptionsPerConnection = 20;
        var connectionIndex = 0;
        var subscriptionsOnCurrentConnection = 0;
        var connectionId = "conn-0";

        for (var i = 0; i < SubscriptionCount; i++)
        {
            if (subscriptionsOnCurrentConnection >= maxSubscriptionsPerConnection)
            {
                connectionIndex++;
                connectionId = $"conn-{connectionIndex}";
                subscriptionsOnCurrentConnection = 0;
            }

            // Authors filter that will never match the benchmark event below: forces
            // FindMatching to actually evaluate every one of these rather than short-
            // circuiting on an earlier, cheaper match somewhere in the collection.
            var filter = new NostrFilter { Kinds = [1], Authors = [$"non-matching-pubkey-{i}"] };
            _registry.TryAddOrReplace(connectionId, $"sub-{i}", [filter]);
            subscriptionsOnCurrentConnection++;
        }

        _publishedEvent = new NostrEvent
        {
            Id = new string('z', 64),
            Pubkey = "the-actual-publishing-pubkey",
            CreatedAt = 1700000000,
            Kind = 1,
            Tags = [],
            Content = "a published event matching none of the registered subscriptions",
            Sig = new string('b', 128),
        };
    }

    [Benchmark]
    public int FindMatchingAgainstNonMatchingEvent()
    {
        var count = 0;
        foreach ((string ConnectionId, string SubscriptionId) _ in _registry.FindMatching(_publishedEvent))
            count++;

        return count;
    }
}
