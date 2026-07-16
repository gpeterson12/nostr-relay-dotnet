using System.Net.WebSockets;
using System.Text.Json;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

/// <summary>
/// Section 3.3's replaceable/ephemeral/addressable persistence rules are already covered
/// thoroughly at the storage layer (Storage.Tests' EventStoreContractTests, 20 tests
/// against SqliteEventStore directly) and ephemeral's live-broadcast path is covered in
/// LiveFanOutTests. What's specifically missing, and what Milestone 5 closes out, is proof
/// that the same guarantees hold end to end through the real WebSocket server: the
/// validation pipeline, NostrConnectionHandler's outcome-to-OK mapping, and the fan-out
/// bus all have to agree with the storage layer, not just the storage layer being correct
/// in isolation.
/// </summary>
public class KindStrategyTests : IAsyncLifetime
{
    private NostrRelayWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new NostrRelayWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReplaceableEvent_NewerVersion_SupersedesOlder_ThroughRealServer()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("replaceable-newer-seed");
        NostrEvent older = SignWithKey(privkey, pubkeyHex, "old profile", kind: 0, createdAt: 100);
        NostrEvent newer = SignWithKey(privkey, pubkeyHex, "new profile", kind: 0, createdAt: 200);

        using WebSocket socket = await _factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(older)}}]""");
        JsonElement firstOk = await socket.ReceiveUntilAsync("OK");
        Assert.True(firstOk[2].GetBoolean());

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(newer)}}]""");
        JsonElement secondOk = await socket.ReceiveUntilAsync("OK");
        Assert.True(secondOk[2].GetBoolean());

        await socket.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"], "kinds": [0]}]""");
        JsonElement eventMessage = await socket.ReceiveUntilAsync("EVENT");
        await socket.ReceiveUntilAsync("EOSE");

        Assert.Equal(newer.Id, eventMessage[2].GetProperty("id").GetString());
        Assert.Equal("new profile", eventMessage[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ReplaceableEvent_OlderVersionArrivingLate_IsAcceptedButDoesNotReplaceStoredNewer()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("replaceable-late-seed");
        NostrEvent newer = SignWithKey(privkey, pubkeyHex, "current profile", kind: 0, createdAt: 200);
        NostrEvent olderLateArrival = SignWithKey(privkey, pubkeyHex, "stale profile", kind: 0, createdAt: 100);

        using WebSocket socket = await _factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(newer)}}]""");
        await socket.ReceiveUntilAsync("OK");

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(olderLateArrival)}}]""");
        JsonElement lateOk = await socket.ReceiveUntilAsync("OK");

        // Per PersistResult's documented mapping: Superseded is still accepted (OK true),
        // it was a well-formed, validly-signed event, it just isn't the copy the relay
        // keeps for this (pubkey, kind) key.
        Assert.True(lateOk[2].GetBoolean());

        await socket.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"], "kinds": [0]}]""");
        JsonElement eventMessage = await socket.ReceiveUntilAsync("EVENT");
        await socket.ReceiveUntilAsync("EOSE");

        Assert.Equal(newer.Id, eventMessage[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ReplaceableEvent_SupersededLateArrival_IsNotBroadcastLiveToSubscribers()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("replaceable-fanout-seed");
        NostrEvent newer = SignWithKey(privkey, pubkeyHex, "current profile", kind: 0, createdAt: 200);
        NostrEvent olderLateArrival = SignWithKey(privkey, pubkeyHex, "stale profile", kind: 0, createdAt: 100);

        using WebSocket subscriber = await _factory.ConnectAsync();
        await subscriber.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"], "kinds": [0]}]""");
        await subscriber.ReceiveUntilAsync("EOSE");

        using WebSocket publisher = await _factory.ConnectAsync();
        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(newer)}}]""");
        await publisher.ReceiveUntilAsync("OK");

        JsonElement delivered = await subscriber.ReceiveUntilAsync("EVENT");
        Assert.Equal(newer.Id, delivered[2].GetProperty("id").GetString());

        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(olderLateArrival)}}]""");
        await publisher.ReceiveUntilAsync("OK");

        // The superseded event must never reach the subscriber: HandleEventAsync only
        // publishes to the bus on Stored/Ephemeral outcomes, not Superseded.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await subscriber.ReceiveArrayAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(500)).Token);
        });
    }

    [Fact]
    public async Task AddressableEvent_DifferentDTags_BothRetrievable_ThroughRealServer()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("addressable-distinct-seed");
        NostrEvent articleOne = SignWithKey(privkey, pubkeyHex, "first article", kind: 30023, tags: [["d", "article-one"]]);
        NostrEvent articleTwo = SignWithKey(privkey, pubkeyHex, "second article", kind: 30023, tags: [["d", "article-two"]]);

        using WebSocket socket = await _factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(articleOne)}}]""");
        await socket.ReceiveUntilAsync("OK");
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(articleTwo)}}]""");
        await socket.ReceiveUntilAsync("OK");

        await socket.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"], "kinds": [30023]}]""");
        JsonElement first = await socket.ReceiveUntilAsync("EVENT");
        JsonElement second = await socket.ReceiveUntilAsync("EVENT");
        await socket.ReceiveUntilAsync("EOSE");

        var receivedIds = new[] { first[2].GetProperty("id").GetString(), second[2].GetProperty("id").GetString() };
        Assert.Contains(articleOne.Id, receivedIds);
        Assert.Contains(articleTwo.Id, receivedIds);
    }

    [Fact]
    public async Task AddressableEvent_SameDTagNewerVersion_SupersedesOlder_ThroughRealServer()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("addressable-supersede-seed");
        NostrEvent v1 = SignWithKey(privkey, pubkeyHex, "draft", kind: 30023, createdAt: 100, tags: [["d", "my-article"]]);
        NostrEvent v2 = SignWithKey(privkey, pubkeyHex, "published", kind: 30023, createdAt: 200, tags: [["d", "my-article"]]);

        using WebSocket socket = await _factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(v1)}}]""");
        await socket.ReceiveUntilAsync("OK");
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(v2)}}]""");
        await socket.ReceiveUntilAsync("OK");

        await socket.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"], "kinds": [30023]}]""");
        JsonElement eventMessage = await socket.ReceiveUntilAsync("EVENT");
        await socket.ReceiveUntilAsync("EOSE");

        Assert.Equal(v2.Id, eventMessage[2].GetProperty("id").GetString());
        Assert.Equal("published", eventMessage[2].GetProperty("content").GetString());
    }
}
