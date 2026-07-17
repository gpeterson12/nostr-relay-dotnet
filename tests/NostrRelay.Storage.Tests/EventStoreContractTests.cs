using NostrRelay.Core;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Storage.Tests;

/// <summary>
/// Exercises <see cref="IEventStore"/> against whatever provider a subclass wires up via
/// <see cref="CreateStoreAsync"/>. Proves behavioral parity across providers by construction:
/// every test here runs unchanged against SQLite now and Postgres once Milestone 6 adds a
/// <c>PostgresEventStoreContractTests : EventStoreContractTests</c>.
///
/// Events here don't need valid signatures: the storage contract explicitly does not
/// re-validate id/signature (that's <see cref="Core.Validation.EventValidationPipeline"/>'s
/// job, upstream of the store), so test events use simple readable placeholder hex-ish
/// strings rather than real cryptographic material.
/// </summary>
public abstract class EventStoreContractTests : IAsyncLifetime
{
    private IEventStore Store { get; set; } = null!;

    protected abstract Task<IEventStore> CreateStoreAsync();

    protected abstract Task DisposeStoreAsync();

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public async Task DisposeAsync() => await DisposeStoreAsync();

    private static int _counter;

    private static NostrEvent MakeEvent(
        string? id = null,
        string pubkey = "pubkey-a",
        long createdAt = 1700000000,
        int kind = 1,
        IReadOnlyList<IReadOnlyList<string>>? tags = null,
        string content = "")
    {
        var uniqueSuffix = Interlocked.Increment(ref _counter);
        return new NostrEvent
        {
            Id = id ?? $"event-{uniqueSuffix}",
            Pubkey = pubkey,
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags ?? [],
            Content = content,
            Sig = "sig-placeholder",
        };
    }

    private async Task<List<NostrEvent>> QueryAllAsync(NostrFilter filter)
    {
        var results = new List<NostrEvent>();
        await foreach (NostrEvent evt in Store.QueryAsync([filter], CancellationToken.None))
            results.Add(evt);
        return results;
    }

    // --- Regular events (Section 3.3) ---

    [Fact]
    public async Task SaveEventAsync_Regular_NewEvent_ReturnsStoredAndIsQueryable()
    {
        NostrEvent evt = MakeEvent(id: "regular-1", kind: 1);

        PersistResult result = await Store.SaveEventAsync(evt, CancellationToken.None);

        Assert.Equal(PersistOutcome.Stored, result.Outcome);
        var found = await QueryAllAsync(new NostrFilter { Ids = ["regular-1"] });
        Assert.Single(found);
    }

    [Fact]
    public async Task SaveEventAsync_Regular_DuplicateId_ReturnsDuplicateAndDoesNotDoubleStore()
    {
        NostrEvent evt = MakeEvent(id: "regular-dup", kind: 1);
        await Store.SaveEventAsync(evt, CancellationToken.None);

        PersistResult second = await Store.SaveEventAsync(evt, CancellationToken.None);

        Assert.Equal(PersistOutcome.Duplicate, second.Outcome);
        var found = await QueryAllAsync(new NostrFilter { Ids = ["regular-dup"] });
        Assert.Single(found);
    }

    [Fact]
    public async Task SaveEventAsync_Regular_MultipleEventsSamePubkeyAndKind_AllRetained()
    {
        // Distinguishes regular from replaceable: no upsert-by-key behavior.
        NostrEvent evt1 = MakeEvent(id: "regular-multi-1", pubkey: "pubkey-b", kind: 1, createdAt: 100);
        NostrEvent evt2 = MakeEvent(id: "regular-multi-2", pubkey: "pubkey-b", kind: 1, createdAt: 200);

        await Store.SaveEventAsync(evt1, CancellationToken.None);
        await Store.SaveEventAsync(evt2, CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-b"], Kinds = [1] });
        Assert.Equal(2, found.Count);
    }

    // --- Replaceable events (Section 3.3) ---

    [Fact]
    public async Task SaveEventAsync_Replaceable_NewerEvent_SupersedesOlder()
    {
        NostrEvent older = MakeEvent(id: "repl-old", pubkey: "pubkey-c", kind: 0, createdAt: 100);
        NostrEvent newer = MakeEvent(id: "repl-new", pubkey: "pubkey-c", kind: 0, createdAt: 200);

        PersistResult firstResult = await Store.SaveEventAsync(older, CancellationToken.None);
        PersistResult secondResult = await Store.SaveEventAsync(newer, CancellationToken.None);

        Assert.Equal(PersistOutcome.Stored, firstResult.Outcome);
        Assert.Equal(PersistOutcome.Stored, secondResult.Outcome);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-c"], Kinds = [0] });
        Assert.Single(found);
        Assert.Equal("repl-new", found[0].Id);
    }

    [Fact]
    public async Task SaveEventAsync_Replaceable_OlderEvent_ReturnsSupersededAndDoesNotReplace()
    {
        NostrEvent newer = MakeEvent(id: "repl-new2", pubkey: "pubkey-d", kind: 3, createdAt: 200);
        NostrEvent older = MakeEvent(id: "repl-old2", pubkey: "pubkey-d", kind: 3, createdAt: 100);

        await Store.SaveEventAsync(newer, CancellationToken.None);
        PersistResult lateArrival = await Store.SaveEventAsync(older, CancellationToken.None);

        Assert.Equal(PersistOutcome.Superseded, lateArrival.Outcome);
        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-d"], Kinds = [3] });
        Assert.Single(found);
        Assert.Equal("repl-new2", found[0].Id);
    }

    [Fact]
    public async Task SaveEventAsync_Replaceable_EqualTimestamp_LowestIdWins()
    {
        // NIP-01: "the event with the lowest id (first in lexical order) should be retained."
        NostrEvent eventA = MakeEvent(id: "aaa-lower", pubkey: "pubkey-e", kind: 0, createdAt: 100);
        NostrEvent eventB = MakeEvent(id: "zzz-higher", pubkey: "pubkey-e", kind: 0, createdAt: 100);

        // Save the lexically-higher id first, then the lower one should still win.
        await Store.SaveEventAsync(eventB, CancellationToken.None);
        PersistResult result = await Store.SaveEventAsync(eventA, CancellationToken.None);

        Assert.Equal(PersistOutcome.Stored, result.Outcome);
        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-e"], Kinds = [0] });
        Assert.Single(found);
        Assert.Equal("aaa-lower", found[0].Id);
    }

    // --- Ephemeral events (Section 3.3) ---

    [Fact]
    public async Task SaveEventAsync_Ephemeral_ReturnsEphemeralAndIsNeverStored()
    {
        NostrEvent evt = MakeEvent(id: "ephemeral-1", kind: 20001);

        PersistResult result = await Store.SaveEventAsync(evt, CancellationToken.None);

        Assert.Equal(PersistOutcome.Ephemeral, result.Outcome);
        var found = await QueryAllAsync(new NostrFilter { Ids = ["ephemeral-1"] });
        Assert.Empty(found);
    }

    // --- Addressable events (Section 3.3) ---

    [Fact]
    public async Task SaveEventAsync_Addressable_DifferentDTags_BothRetained()
    {
        NostrEvent article1 = MakeEvent(id: "addr-1", pubkey: "pubkey-f", kind: 30023, tags: [["d", "article-one"]]);
        NostrEvent article2 = MakeEvent(id: "addr-2", pubkey: "pubkey-f", kind: 30023, tags: [["d", "article-two"]]);

        await Store.SaveEventAsync(article1, CancellationToken.None);
        await Store.SaveEventAsync(article2, CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-f"], Kinds = [30023] });
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task SaveEventAsync_Addressable_SameDTagNewerVersion_ReplacesOlder()
    {
        NostrEvent v1 = MakeEvent(id: "addr-v1", pubkey: "pubkey-g", kind: 30023, createdAt: 100, tags: [["d", "my-article"]]);
        NostrEvent v2 = MakeEvent(id: "addr-v2", pubkey: "pubkey-g", kind: 30023, createdAt: 200, tags: [["d", "my-article"]]);

        await Store.SaveEventAsync(v1, CancellationToken.None);
        await Store.SaveEventAsync(v2, CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-g"], Kinds = [30023] });
        Assert.Single(found);
        Assert.Equal("addr-v2", found[0].Id);
    }

    [Fact]
    public async Task SaveEventAsync_Addressable_MissingDTag_TreatedAsEmptyStringKey()
    {
        NostrEvent v1 = MakeEvent(id: "addr-nodtag-1", pubkey: "pubkey-h", kind: 30023, createdAt: 100);
        NostrEvent v2 = MakeEvent(id: "addr-nodtag-2", pubkey: "pubkey-h", kind: 30023, createdAt: 200);

        await Store.SaveEventAsync(v1, CancellationToken.None);
        PersistResult result = await Store.SaveEventAsync(v2, CancellationToken.None);

        Assert.Equal(PersistOutcome.Stored, result.Outcome);
        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-h"], Kinds = [30023] });
        Assert.Single(found);
        Assert.Equal("addr-nodtag-2", found[0].Id);
    }

    // --- Querying ---

    [Fact]
    public async Task QueryAsync_FiltersByKind()
    {
        await Store.SaveEventAsync(MakeEvent(id: "kind-1a", pubkey: "pubkey-i", kind: 1), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(id: "kind-2a", pubkey: "pubkey-i", kind: 2), CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-i"], Kinds = [1] });

        Assert.Single(found);
        Assert.Equal("kind-1a", found[0].Id);
    }

    [Fact]
    public async Task QueryAsync_FiltersByTag()
    {
        NostrEvent tagged = MakeEvent(id: "tag-yes", pubkey: "pubkey-j", tags: [["e", "referenced-event"]]);
        NostrEvent untagged = MakeEvent(id: "tag-no", pubkey: "pubkey-j");

        await Store.SaveEventAsync(tagged, CancellationToken.None);
        await Store.SaveEventAsync(untagged, CancellationToken.None);

        var filter = new NostrFilter
        {
            Authors = ["pubkey-j"],
            TagFilters = new Dictionary<char, IReadOnlyList<string>> { ['e'] = ["referenced-event"] },
        };
        var found = await QueryAllAsync(filter);

        Assert.Single(found);
        Assert.Equal("tag-yes", found[0].Id);
    }

    [Fact]
    public async Task QueryAsync_RespectsSinceAndUntil()
    {
        await Store.SaveEventAsync(MakeEvent(id: "time-early", pubkey: "pubkey-k", createdAt: 100), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(id: "time-mid", pubkey: "pubkey-k", createdAt: 200), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(id: "time-late", pubkey: "pubkey-k", createdAt: 300), CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-k"], Since = 150, Until = 250 });

        Assert.Single(found);
        Assert.Equal("time-mid", found[0].Id);
    }

    [Fact]
    public async Task QueryAsync_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
            await Store.SaveEventAsync(MakeEvent(pubkey: "pubkey-l", createdAt: 100 + i), CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-l"], Limit = 2 });

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task QueryAsync_OrdersMostRecentFirst()
    {
        await Store.SaveEventAsync(MakeEvent(id: "order-1", pubkey: "pubkey-m", createdAt: 100), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(id: "order-2", pubkey: "pubkey-m", createdAt: 300), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(id: "order-3", pubkey: "pubkey-m", createdAt: 200), CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-m"] });

        Assert.Equal(["order-2", "order-3", "order-1"], found.Select(e => e.Id));
    }

    [Fact]
    public async Task QueryAsync_MultipleFilters_AreOrdTogetherAndDeduplicated()
    {
        NostrEvent evt = MakeEvent(id: "multi-filter-shared", pubkey: "pubkey-n", kind: 1);
        await Store.SaveEventAsync(evt, CancellationToken.None);

        // Both filters match the same event; it should only be yielded once.
        var results = new List<NostrEvent>();
        await foreach (NostrEvent e in Store.QueryAsync(
            [
                new NostrFilter { Ids = ["multi-filter-shared"] },
                new NostrFilter { Authors = ["pubkey-n"] },
            ],
            CancellationToken.None))
        {
            results.Add(e);
        }

        Assert.Single(results);
    }

    // --- Counting ---

    [Fact]
    public async Task CountAsync_ReturnsNumberOfMatchingEvents()
    {
        await Store.SaveEventAsync(MakeEvent(pubkey: "pubkey-o", kind: 1), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(pubkey: "pubkey-o", kind: 1), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(pubkey: "pubkey-o", kind: 2), CancellationToken.None);

        var count = await Store.CountAsync(new NostrFilter { Authors = ["pubkey-o"], Kinds = [1] }, CancellationToken.None);

        Assert.Equal(2, count);
    }

    // --- Deletion ---

    [Fact]
    public async Task DeleteEventsAsync_RemovesSpecifiedEvents()
    {
        await Store.SaveEventAsync(MakeEvent(id: "delete-me", pubkey: "pubkey-p"), CancellationToken.None);
        await Store.SaveEventAsync(MakeEvent(id: "keep-me", pubkey: "pubkey-p"), CancellationToken.None);

        await Store.DeleteEventsAsync(["delete-me"], CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-p"] });
        Assert.Single(found);
        Assert.Equal("keep-me", found[0].Id);
    }

    [Fact]
    public async Task DeleteEventsAsync_EmptyList_DoesNothing()
    {
        await Store.SaveEventAsync(MakeEvent(id: "untouched", pubkey: "pubkey-q"), CancellationToken.None);

        await Store.DeleteEventsAsync([], CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-q"] });
        Assert.Single(found);
    }

    // --- NIP-09 deletion requests ---

    [Fact]
    public async Task DeleteEventsAuthoredByAsync_RemovesEventWhenAuthorMatches()
    {
        await Store.SaveEventAsync(MakeEvent(id: "own-post", pubkey: "pubkey-u"), CancellationToken.None);

        await Store.DeleteEventsAuthoredByAsync(["own-post"], "pubkey-u", CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Ids = ["own-post"] });
        Assert.Empty(found);
    }

    [Fact]
    public async Task DeleteEventsAuthoredByAsync_DoesNotRemoveEventWhenAuthorDiffers()
    {
        await Store.SaveEventAsync(MakeEvent(id: "someone-elses-post", pubkey: "pubkey-v"), CancellationToken.None);

        // A different pubkey attempting to delete pubkey-v's event.
        await Store.DeleteEventsAuthoredByAsync(["someone-elses-post"], "pubkey-impersonator", CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Ids = ["someone-elses-post"] });
        Assert.Single(found);
    }

    [Fact]
    public async Task DeleteEventsAuthoredByAsync_DoesNotDeleteKind5EventsEvenWhenAuthorMatches()
    {
        // NIP-09: "Publishing a deletion request event against a deletion request has no effect."
        await Store.SaveEventAsync(MakeEvent(id: "a-deletion-request", pubkey: "pubkey-w", kind: 5), CancellationToken.None);

        await Store.DeleteEventsAuthoredByAsync(["a-deletion-request"], "pubkey-w", CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Ids = ["a-deletion-request"] });
        Assert.Single(found);
    }

    [Fact]
    public async Task DeleteEventsAuthoredByAsync_EmptyList_DoesNothing()
    {
        await Store.SaveEventAsync(MakeEvent(id: "untouched-2", pubkey: "pubkey-x"), CancellationToken.None);

        await Store.DeleteEventsAuthoredByAsync([], "pubkey-x", CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Ids = ["untouched-2"] });
        Assert.Single(found);
    }

    [Fact]
    public async Task DeleteAddressableEventAsync_RemovesMatchingCoordinateAtOrBeforeCreatedAt()
    {
        await Store.SaveEventAsync(
            MakeEvent(id: "article-v1", pubkey: "pubkey-y", kind: 30023, createdAt: 100, tags: [["d", "my-article"]]),
            CancellationToken.None);

        await Store.DeleteAddressableEventAsync("pubkey-y", 30023, "my-article", upToCreatedAt: 200, CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-y"], Kinds = [30023] });
        Assert.Empty(found);
    }

    [Fact]
    public async Task DeleteAddressableEventAsync_DoesNotRemoveNewerStoredVersion()
    {
        // A legitimate update racing ahead of an older deletion request shouldn't be
        // undone by it.
        await Store.SaveEventAsync(
            MakeEvent(id: "article-v2", pubkey: "pubkey-z", kind: 30023, createdAt: 300, tags: [["d", "my-article"]]),
            CancellationToken.None);

        await Store.DeleteAddressableEventAsync("pubkey-z", 30023, "my-article", upToCreatedAt: 200, CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-z"], Kinds = [30023] });
        Assert.Single(found);
        Assert.Equal("article-v2", found[0].Id);
    }

    [Fact]
    public async Task DeleteAddressableEventAsync_NoMatchingCoordinate_IsNoOp()
    {
        await Store.SaveEventAsync(
            MakeEvent(id: "unrelated-article", pubkey: "pubkey-aa", kind: 30023, tags: [["d", "other-article"]]),
            CancellationToken.None);

        await Store.DeleteAddressableEventAsync("pubkey-aa", 30023, "my-article", upToCreatedAt: long.MaxValue, CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-aa"], Kinds = [30023] });
        Assert.Single(found);
    }

    // --- NIP-40 expiration ---

    [Fact]
    public async Task QueryAsync_ExcludesExpiredEvents_EvenBeforeSweepRuns()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();

        await Store.SaveEventAsync(
            MakeEvent(id: "already-expired", pubkey: "pubkey-bb", tags: [["expiration", past.ToString()]]), CancellationToken.None);

        // Deliberately no DeleteExpiredEventsAsync call: NIP-40 requires expired events to
        // be excluded from query results regardless of whether the sweep has run yet.
        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-bb"] });

        Assert.Empty(found);
    }

    [Fact]
    public async Task CountAsync_ExcludesExpiredEvents_EvenBeforeSweepRuns()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();

        await Store.SaveEventAsync(
            MakeEvent(pubkey: "pubkey-cc", tags: [["expiration", past.ToString()]]), CancellationToken.None);

        var count = await Store.CountAsync(new NostrFilter { Authors = ["pubkey-cc"] }, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteExpiredEventsAsync_RemovesOnlyExpiredEvents()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();

        await Store.SaveEventAsync(
            MakeEvent(id: "expired", pubkey: "pubkey-r", tags: [["expiration", past.ToString()]]), CancellationToken.None);
        await Store.SaveEventAsync(
            MakeEvent(id: "not-expired", pubkey: "pubkey-r", tags: [["expiration", future.ToString()]]), CancellationToken.None);
        await Store.SaveEventAsync(
            MakeEvent(id: "no-expiration", pubkey: "pubkey-r"), CancellationToken.None);

        await Store.DeleteExpiredEventsAsync(CancellationToken.None);

        var found = await QueryAllAsync(new NostrFilter { Authors = ["pubkey-r"] });
        Assert.Equal(["no-expiration", "not-expired"], found.Select(e => e.Id).OrderBy(id => id));
    }
}