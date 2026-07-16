using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Core.Protocol;
using NostrRelay.Core.Serialization;
using SHA256 = System.Security.Cryptography.SHA256;

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

    private async Task<WebSocket> ConnectAsync()
    {
        WebSocketClient client = _factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/"), CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReceiveArrayAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement;
    }

    /// <summary>Reads messages until one whose type (index 0) matches <paramref name="type"/>,
    /// skipping others (e.g. an EOSE that legitimately arrives before a live EVENT). Bounded
    /// by a timeout so a missing message fails the test instead of hanging forever.</summary>
    private static async Task<JsonElement> ReceiveUntilAsync(WebSocket socket, string type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            JsonElement message = await ReceiveArrayAsync(socket, cts.Token);
            if (message[0].GetString() == type)
                return message;
        }
    }

    private static string ToEventJson(NostrEvent evt) => JsonSerializer.Serialize(evt, NostrJsonOptions.Default);

    private static (ECPrivKey PrivKey, string PubkeyHex) GenerateKeyPair(string seed)
    {
        var privkeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + Guid.NewGuid()));
        var privkey = ECPrivKey.Create(privkeyBytes);
        var pubkeyHex = Convert.ToHexStringLower(privkey.CreateXOnlyPubKey().ToBytes());
        return (privkey, pubkeyHex);
    }

    private static NostrEvent SignWithKey(
        ECPrivKey privkey, string pubkeyHex, string content, int kind = 1, long createdAt = 1700000000)
    {
        IReadOnlyList<IReadOnlyList<string>> tags = [];
        var id = NostrEventCanonicalSerializer.ComputeId(pubkeyHex, createdAt, kind, tags, content);
        var sigHex = Convert.ToHexStringLower(privkey.SignBIP340(Convert.FromHexString(id)).ToBytes());

        return new NostrEvent
        {
            Id = id,
            Pubkey = pubkeyHex,
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags,
            Content = content,
            Sig = sigHex,
        };
    }

    private static (NostrEvent Event, string PubkeyHex) SignEvent(string content, int kind = 1, string seed = "fanout-test-seed")
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair(seed);
        return (SignWithKey(privkey, pubkeyHex, content, kind), pubkeyHex);
    }

    [Fact]
    public async Task EventPublishedAfterSubscription_IsDeliveredLiveToOpenSubscriber()
    {
        (NostrEvent evt, var pubkeyHex) = SignEvent("live delivery test");

        using WebSocket subscriber = await ConnectAsync();
        await SendAsync(subscriber, $$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        await ReceiveUntilAsync(subscriber, "EOSE"); // no historical matches yet; subscription is now live

        using WebSocket publisher = await ConnectAsync();
        await SendAsync(publisher, $$"""["EVENT", {{ToEventJson(evt)}}]""");
        await ReceiveUntilAsync(publisher, "OK");

        JsonElement delivered = await ReceiveUntilAsync(subscriber, "EVENT");

        Assert.Equal("sub1", delivered[1].GetString());
        Assert.Equal(evt.Id, delivered[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task NonMatchingSubscription_DoesNotReceiveUnrelatedEvent()
    {
        (NostrEvent matchingEvt, var matchingPubkey) = SignEvent("for the subscriber");
        (NostrEvent otherEvt, _) = SignEvent("not for the subscriber");

        using WebSocket subscriber = await ConnectAsync();
        await SendAsync(subscriber, $$"""["REQ", "sub1", {"authors": ["{{matchingPubkey}}"]}]""");
        await ReceiveUntilAsync(subscriber, "EOSE");

        using WebSocket publisher = await ConnectAsync();
        await SendAsync(publisher, $$"""["EVENT", {{ToEventJson(otherEvt)}}]""");
        await ReceiveUntilAsync(publisher, "OK");
        await SendAsync(publisher, $$"""["EVENT", {{ToEventJson(matchingEvt)}}]""");
        await ReceiveUntilAsync(publisher, "OK");

        // Only the matching event should ever arrive; if the non-matching one were
        // (incorrectly) delivered, it would arrive first, since it was published first.
        JsonElement delivered = await ReceiveUntilAsync(subscriber, "EVENT");
        Assert.Equal(matchingEvt.Id, delivered[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Close_StopsFurtherLiveDelivery()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("close-test-seed");
        NostrEvent firstEvt = SignWithKey(privkey, pubkeyHex, "before close");

        using WebSocket subscriber = await ConnectAsync();
        await SendAsync(subscriber, $$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        await ReceiveUntilAsync(subscriber, "EOSE");

        using WebSocket publisher = await ConnectAsync();
        await SendAsync(publisher, $$"""["EVENT", {{ToEventJson(firstEvt)}}]""");
        await ReceiveUntilAsync(publisher, "OK");
        await ReceiveUntilAsync(subscriber, "EVENT"); // confirm live delivery works before closing

        await SendAsync(subscriber, """["CLOSE", "sub1"]""");
        await Task.Delay(200); // let the server process CLOSE before the next publish

        // Same keypair, genuinely signed, only the content (and therefore id) differs -
        // proves CLOSE itself, not a filter/signature mismatch, is what stops delivery.
        NostrEvent secondEvt = SignWithKey(privkey, pubkeyHex, "after close", createdAt: 1700000001);

        await SendAsync(publisher, $$"""["EVENT", {{ToEventJson(secondEvt)}}]""");
        JsonElement response = await ReceiveUntilAsync(publisher, "OK");

        // The event itself is still accepted by the relay (CLOSE only affects the
        // subscriber's subscription); it's specifically absent from the subscriber's
        // socket that we're testing here.
        Assert.True(response[2].GetBoolean());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReceiveArrayAsync(subscriber, cts.Token);
        });
    }

    [Fact]
    public async Task EphemeralEvent_IsDeliveredLiveButNeverPersisted()
    {
        (NostrEvent evt, var pubkeyHex) = SignEvent("ephemeral broadcast", kind: 20001);

        using WebSocket subscriber = await ConnectAsync();
        await SendAsync(subscriber, $$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        await ReceiveUntilAsync(subscriber, "EOSE");

        using WebSocket publisher = await ConnectAsync();
        await SendAsync(publisher, $$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement ok = await ReceiveUntilAsync(publisher, "OK");
        Assert.True(ok[2].GetBoolean());

        JsonElement delivered = await ReceiveUntilAsync(subscriber, "EVENT");
        Assert.Equal(evt.Id, delivered[2].GetProperty("id").GetString());

        // Confirm it was never actually stored: a fresh REQ against the same author
        // should find nothing historically, since ephemeral events are broadcast-only.
        // Read the very next message directly (not ReceiveUntilAsync's skip-to-EOSE),
        // so an incorrectly-delivered historical EVENT would fail this assertion instead
        // of being silently skipped past.
        using WebSocket lateSubscriber = await ConnectAsync();
        await SendAsync(lateSubscriber, $$"""["REQ", "sub2", {"authors": ["{{pubkeyHex}}"]}]""");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonElement firstResponse = await ReceiveArrayAsync(lateSubscriber, cts.Token);
        Assert.Equal("EOSE", firstResponse[0].GetString());
        Assert.Equal("sub2", firstResponse[1].GetString());
    }
}