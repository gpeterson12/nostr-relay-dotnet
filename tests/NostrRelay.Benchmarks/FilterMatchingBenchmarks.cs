using BenchmarkDotNet.Attributes;
using NostrRelay.Core;

namespace NostrRelay.Benchmarks;

/// <summary>
/// Section 7: "BenchmarkDotNet micro-benchmarks for signature verification and filter
/// matching." Three filter shapes of increasing cost against the same matching event, to
/// see where NostrFilter.Matches actually spends its time: a bare kind check, a filter
/// with tag conditions (which linearly scans the event's tags per condition), and a
/// filter combining several condition types at once, closer to what a real client
/// subscription tends to look like.
/// </summary>
[MemoryDiagnoser]
public class FilterMatchingBenchmarks
{
    private NostrFilter _simpleFilter = null!;
    private NostrFilter _tagFilter = null!;
    private NostrFilter _multiConditionFilter = null!;
    private NostrEvent _matchingEvent = null!;

    [GlobalSetup]
    public void Setup()
    {
        _simpleFilter = new NostrFilter { Kinds = [1] };

        _tagFilter = new NostrFilter
        {
            Kinds = [1],
            TagFilters = new Dictionary<char, IReadOnlyList<string>> { ['e'] = ["ref-1", "ref-2", "ref-3"] },
        };

        _multiConditionFilter = new NostrFilter
        {
            Kinds = [1],
            Authors = [new string('a', 64)],
            Since = 1600000000,
            Until = 1800000000,
            TagFilters = new Dictionary<char, IReadOnlyList<string>>
            {
                ['e'] = ["ref-1"],
                ['p'] = ["ref-2"],
            },
        };

        _matchingEvent = new NostrEvent
        {
            Id = new string('z', 64),
            Pubkey = new string('a', 64),
            CreatedAt = 1700000000,
            Kind = 1,
            Tags = [["e", "ref-1"], ["p", "ref-2"], ["t", "nostr"]],
            Content = "benchmark event content",
            Sig = new string('b', 128),
        };
    }

    [Benchmark(Baseline = true)]
    public bool SimpleKindMatch() => _simpleFilter.Matches(_matchingEvent);

    [Benchmark]
    public bool TagFilterMatch() => _tagFilter.Matches(_matchingEvent);

    [Benchmark]
    public bool MultiConditionMatch() => _multiConditionFilter.Matches(_matchingEvent);
}
