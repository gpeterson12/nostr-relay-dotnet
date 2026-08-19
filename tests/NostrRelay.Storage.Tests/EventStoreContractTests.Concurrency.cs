using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Tests;

/// <summary>
/// Concurrent-writer half of the storage contract. These run unchanged against both
/// providers, which is the point: SQLite and Postgres reach the same guarantees by
/// genuinely different means (a whole-database <c>BEGIN IMMEDIATE</c> write lock versus a
/// per-key <c>pg_advisory_xact_lock</c>), and the only way to know both actually hold is to
/// assert the same invariants against both under real contention.
///
/// Every test here fails if the store falls back to lookup-then-write without serializing:
/// the read-check-delete-insert sequence for replaceable and addressable events has a
/// window between the lookup and the insert, and without a lock two writers can both
/// conclude they are first. The regular-event test covers the other race, two writers
/// inserting the same id, where the constraint-violation catch is the correctness fallback
/// rather than the common path.
///
/// Writers are released from a shared gate so they contend rather than trickling through
/// one at a time. Assertions are on the converged end state, never on which individual
/// writer happened to win a race, so there is nothing timing-dependent to go flaky in CI.
/// </summary>
public abstract partial class EventStoreContractTests
{
    private const int ConcurrentWriters = 8;

    /// <summary>
    /// Runs <paramref name="work"/> once per index, with every invocation held at a gate
    /// until all of them are queued, so the operations genuinely overlap instead of
    /// completing in sequence.
    /// </summary>
    private static async Task<TResult[]> RunConcurrentlyAsync<TResult>(int count, Func<int, Task<TResult>> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await work(i);
            }))
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task SaveEventAsync_Regular_ConcurrentIdenticalIds_StoresExactlyOneCopy()
    {
        NostrEvent evt = MakeEvent(id: "concurrent-regular", kind: 1);

        var results = await RunConcurrentlyAsync(
            ConcurrentWriters, _ => Store.SaveEventAsync(evt, CancellationToken.None));

        // Exactly one writer may report Stored; every other must report Duplicate, whether
        // it lost at the up-front existence check or at the unique-constraint fallback.
        Assert.Equal(1, results.Count(r => r.Outcome == PersistOutcome.Stored));
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { PersistOutcome.Stored, PersistOutcome.Duplicate }));

        var stored = await QueryAllAsync(new NostrFilter { Ids = ["concurrent-regular"] });
        Assert.Single(stored);
    }

    [Fact]
    public async Task SaveEventAsync_Replaceable_ConcurrentWritesForSameKey_ConvergeOnNewestCreatedAt()
    {
        const long baseCreatedAt = 1700000000;
        const string pubkey = "concurrent-replaceable-author";

        await RunConcurrentlyAsync(ConcurrentWriters, i => Store.SaveEventAsync(
            MakeEvent(id: $"replaceable-{i}", pubkey: pubkey, createdAt: baseCreatedAt + i, kind: 0),
            CancellationToken.None));

        var stored = await QueryAllAsync(new NostrFilter { Authors = [pubkey], Kinds = [0] });

        // One row for the (pubkey, kind) key, no matter how the writes interleaved: a
        // second row here means two writers both passed the lookup believing they were
        // first.
        NostrEvent survivor = Assert.Single(stored);
        Assert.Equal(baseCreatedAt + ConcurrentWriters - 1, survivor.CreatedAt);
    }

    [Fact]
    public async Task SaveEventAsync_Replaceable_ConcurrentWritesWithSameCreatedAt_ConvergeOnLowestId()
    {
        const long sharedCreatedAt = 1700000000;
        const string pubkey = "concurrent-tiebreak-author";

        await RunConcurrentlyAsync(ConcurrentWriters, i => Store.SaveEventAsync(
            MakeEvent(id: $"tiebreak-{i:D4}", pubkey: pubkey, createdAt: sharedCreatedAt, kind: 0),
            CancellationToken.None));

        var stored = await QueryAllAsync(new NostrFilter { Authors = [pubkey], Kinds = [0] });

        // NIP-01 breaks created_at ties by lowest id in lexical order, and that rule has to
        // survive contention or two relays replaying the same events could disagree about
        // which copy is current.
        NostrEvent survivor = Assert.Single(stored);
        Assert.Equal("tiebreak-0000", survivor.Id);
    }

    [Fact]
    public async Task SaveEventAsync_Addressable_ConcurrentWritesForSameCoordinate_ConvergeOnNewestCreatedAt()
    {
        const long baseCreatedAt = 1700000000;
        const string pubkey = "concurrent-addressable-author";
        IReadOnlyList<IReadOnlyList<string>> tags = [["d", "profile"]];

        await RunConcurrentlyAsync(ConcurrentWriters, i => Store.SaveEventAsync(
            MakeEvent(id: $"addressable-{i}", pubkey: pubkey, createdAt: baseCreatedAt + i, kind: 30000, tags: tags),
            CancellationToken.None));

        var stored = await QueryAllAsync(new NostrFilter { Authors = [pubkey], Kinds = [30000] });

        NostrEvent survivor = Assert.Single(stored);
        Assert.Equal(baseCreatedAt + ConcurrentWriters - 1, survivor.CreatedAt);
    }

    [Fact]
    public async Task SaveEventAsync_Addressable_ConcurrentWritesForDistinctCoordinates_AllSurvive()
    {
        const string pubkey = "concurrent-distinct-author";

        await RunConcurrentlyAsync(ConcurrentWriters, i => Store.SaveEventAsync(
            MakeEvent(
                id: $"distinct-{i}",
                pubkey: pubkey,
                kind: 30000,
                tags: [["d", $"coordinate-{i}"]]),
            CancellationToken.None));

        var stored = await QueryAllAsync(new NostrFilter { Authors = [pubkey], Kinds = [30000] });

        // Different d tags are different keys, so serializing writes must not also
        // serialize them into overwriting each other. This is the test that would catch a
        // lock keyed too coarsely (per-kind, say, instead of per-coordinate).
        Assert.Equal(ConcurrentWriters, stored.Count);
    }

    [Fact]
    public async Task QueryAsync_ConcurrentWithWrites_ReturnsConsistentResultsWithoutFaulting()
    {
        const string pubkey = "concurrent-reader-author";

        var writes = RunConcurrentlyAsync(ConcurrentWriters, i => Store.SaveEventAsync(
            MakeEvent(id: $"reader-race-{i}", pubkey: pubkey, createdAt: 1700000000 + i, kind: 1),
            CancellationToken.None));

        // Readers run against a database being actively written to. Under SQLite's WAL
        // journal and Postgres's MVCC each snapshot is consistent, so the only thing being
        // asserted is that a reader never faults and never sees a partial row; how many
        // events any given pass observes is legitimately nondeterministic.
        var reads = RunConcurrentlyAsync(ConcurrentWriters, async _ =>
        {
            var observed = await QueryAllAsync(new NostrFilter { Authors = [pubkey], Kinds = [1] });
            Assert.All(observed, evt => Assert.Equal(pubkey, evt.Pubkey));
            return observed.Count;
        });

        await Task.WhenAll(writes, reads);

        var final = await QueryAllAsync(new NostrFilter { Authors = [pubkey], Kinds = [1] });
        Assert.Equal(ConcurrentWriters, final.Count);
    }
}
