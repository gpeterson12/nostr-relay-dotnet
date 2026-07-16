using System.Net.WebSockets;
using System.Text.Json;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

public class LiveFanOutTests : IAsyncLifetime
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
    public async Task EventPublishedAfterSubscription_IsDeliveredLiveToOpenSubscriber()
    {
        (NostrEvent evt, var pubkeyHex) = SignEvent("live delivery test");

        using WebSocket subscriber = await _factory.ConnectAsync();
        await subscriber.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        await subscriber.ReceiveUntilAsync("EOSE"); // no historical matches yet; subscription is now live

        using WebSocket publisher = await _factory.ConnectAsync();
        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        await publisher.ReceiveUntilAsync("OK");

        JsonElement delivered = await subscriber.ReceiveUntilAsync("EVENT");

        Assert.Equal("sub1", delivered[1].GetString());
        Assert.Equal(evt.Id, delivered[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task NonMatchingSubscription_DoesNotReceiveUnrelatedEvent()
    {
        (NostrEvent matchingEvt, var matchingPubkey) = SignEvent("for the subscriber");
        (NostrEvent otherEvt, _) = SignEvent("not for the subscriber");

        using WebSocket subscriber = await _factory.ConnectAsync();
        await subscriber.SendAsync($$"""["REQ", "sub1", {"authors": ["{{matchingPubkey}}"]}]""");
        await subscriber.ReceiveUntilAsync("EOSE");

        using WebSocket publisher = await _factory.ConnectAsync();
        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(otherEvt)}}]""");
        await publisher.ReceiveUntilAsync("OK");
        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(matchingEvt)}}]""");
        await publisher.ReceiveUntilAsync("OK");

        // Only the matching event should ever arrive; if the non-matching one were
        // (incorrectly) delivered, it would arrive first, since it was published first.
        JsonElement delivered = await subscriber.ReceiveUntilAsync("EVENT");
        Assert.Equal(matchingEvt.Id, delivered[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Close_StopsFurtherLiveDelivery()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("close-test-seed");
        NostrEvent firstEvt = SignWithKey(privkey, pubkeyHex, "before close");

        using WebSocket subscriber = await _factory.ConnectAsync();
        await subscriber.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        await subscriber.ReceiveUntilAsync("EOSE");

        using WebSocket publisher = await _factory.ConnectAsync();
        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(firstEvt)}}]""");
        await publisher.ReceiveUntilAsync("OK");
        await subscriber.ReceiveUntilAsync("EVENT"); // confirm live delivery works before closing

        await subscriber.SendAsync("""["CLOSE", "sub1"]""");
        await Task.Delay(200); // let the server process CLOSE before the next publish

        // Same keypair, genuinely signed, only the content (and therefore id) differs -
        // proves CLOSE itself, not a filter/signature mismatch, is what stops delivery.
        NostrEvent secondEvt = SignWithKey(privkey, pubkeyHex, "after close", createdAt: 1700000001);

        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(secondEvt)}}]""");
        JsonElement response = await publisher.ReceiveUntilAsync("OK");

        // The event itself is still accepted by the relay (CLOSE only affects the
        // subscriber's subscription); it's specifically absent from the subscriber's
        // socket that we're testing here.
        Assert.True(response[2].GetBoolean());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await subscriber.ReceiveArrayAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(500)).Token);
        });
    }

    [Fact]
    public async Task EphemeralEvent_IsDeliveredLiveButNeverPersisted()
    {
        (NostrEvent evt, var pubkeyHex) = SignEvent("ephemeral broadcast", kind: 20001);

        using WebSocket subscriber = await _factory.ConnectAsync();
        await subscriber.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        await subscriber.ReceiveUntilAsync("EOSE");

        using WebSocket publisher = await _factory.ConnectAsync();
        await publisher.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement ok = await publisher.ReceiveUntilAsync("OK");
        Assert.True(ok[2].GetBoolean());

        JsonElement delivered = await subscriber.ReceiveUntilAsync("EVENT");
        Assert.Equal(evt.Id, delivered[2].GetProperty("id").GetString());

        // Confirm it was never actually stored: a fresh REQ against the same author
        // should find nothing historically, since ephemeral events are broadcast-only.
        // Read the very next message directly (not ReceiveUntilAsync's skip-to-EOSE),
        // so an incorrectly-delivered historical EVENT would fail this assertion instead
        // of being silently skipped past.
        using WebSocket lateSubscriber = await _factory.ConnectAsync();
        await lateSubscriber.SendAsync($$"""["REQ", "sub2", {"authors": ["{{pubkeyHex}}"]}]""");
        JsonElement firstResponse = await lateSubscriber.ReceiveArrayAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal("EOSE", firstResponse[0].GetString());
        Assert.Equal("sub2", firstResponse[1].GetString());
    }
}