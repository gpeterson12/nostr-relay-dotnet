using System.Net.WebSockets;
using System.Text.Json;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

public class ExpirationTests
{
    [Fact]
    public async Task Expiration_EventWithFutureExpiration_IsAcceptedAndQueryable()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("expiration-future-seed");
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
        NostrEvent evt = SignWithKey(privkey, pubkeyHex, "expires later", tags: [["expiration", future]]);

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement ok = await socket.ReceiveUntilAsync("OK");
        Assert.True(ok[2].GetBoolean());

        await socket.SendAsync($$"""["REQ", "sub1", {"ids": ["{{evt.Id}}"]}]""");
        JsonElement found = await socket.ReceiveUntilAsync("EVENT");
        Assert.Equal(evt.Id, found[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Expiration_EventAlreadyExpired_IsRejectedAtWriteTime()
    {
        // NIP-40: "Relays SHOULD drop any events that are published to them if they are
        // expired." Not stored at all, not just hidden from queries.
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("expiration-already-past-seed");
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();
        NostrEvent evt = SignWithKey(privkey, pubkeyHex, "already expired on arrival", tags: [["expiration", past]]);

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.False(response[2].GetBoolean());
        Assert.StartsWith("invalid:", response[3].GetString());
        Assert.Contains("expired", response[3].GetString());
    }

    [Fact]
    public async Task Expiration_EventThatExpiresWhileStored_IsExcludedFromQueryOnceExpired()
    {
        // NIP-40: "Relays SHOULD NOT send expired events to clients, even if they are
        // stored." This has to hold independent of the background sweep, which by
        // default runs every few minutes, so this test proves query-time filtering
        // specifically: the event is valid (and queryable) at publish time, expires a
        // couple of seconds later, and must disappear from query results well before any
        // sweep would plausibly have run.
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("expiration-query-exclusion-seed");
        var almostExpired = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds().ToString();
        NostrEvent evt = SignWithKey(privkey, pubkeyHex, "will expire soon", tags: [["expiration", almostExpired]]);

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement ok = await socket.ReceiveUntilAsync("OK");
        Assert.True(ok[2].GetBoolean());

        await socket.SendAsync($$"""["REQ", "sub1", {"ids": ["{{evt.Id}}"]}]""");
        JsonElement found = await socket.ReceiveUntilAsync("EVENT");
        Assert.Equal(evt.Id, found[2].GetProperty("id").GetString());

        await Task.Delay(TimeSpan.FromSeconds(3));

        await socket.SendAsync($$"""["REQ", "sub2", {"ids": ["{{evt.Id}}"]}]""");
        JsonElement response = await socket.ReceiveUntilAsync("EOSE");
        Assert.Equal("EOSE", response[0].GetString());
    }
}
