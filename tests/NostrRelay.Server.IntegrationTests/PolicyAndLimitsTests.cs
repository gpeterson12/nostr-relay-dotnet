using System.Net.WebSockets;
using System.Text.Json;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

public class PolicyAndLimitsTests
{
    [Fact]
    public async Task PubkeyBlocklist_RejectsEventFromBlockedPubkey()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("blocklist-test-seed");
        NostrEvent evt = SignWithKey(privkey, pubkeyHex, "should be blocked");

        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Policy:PubkeyBlocklist:0"] = pubkeyHex,
        });
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.False(response[2].GetBoolean());
        Assert.StartsWith("blocked:", response[3].GetString());
    }

    [Fact]
    public async Task PubkeyAllowlist_RejectsEventFromPubkeyNotOnList()
    {
        var (_, allowlistedPubkey) = GenerateKeyPair("allowlist-member-seed");
        (ECPrivKey otherPrivkey, var otherPubkey) = GenerateKeyPair("allowlist-outsider-seed");
        NostrEvent evt = SignWithKey(otherPrivkey, otherPubkey, "not on the list");

        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Policy:PubkeyAllowlist:0"] = allowlistedPubkey,
        });
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.False(response[2].GetBoolean());
        Assert.Contains("allowlist", response[3].GetString());
    }

    [Fact]
    public async Task PubkeyAllowlist_AcceptsEventFromPubkeyOnList()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("allowlist-member-seed-2");
        NostrEvent evt = SignWithKey(privkey, pubkeyHex, "on the list");

        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Policy:PubkeyAllowlist:0"] = pubkeyHex,
        });
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.True(response[2].GetBoolean());
    }

    [Fact]
    public async Task KindBlocklist_RejectsBlockedKindButAcceptsOthers()
    {
        (NostrEvent blockedEvt, var pubkeyHex) = SignEvent("blocked kind", kind: 4);

        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Policy:KindBlocklist:0"] = "4",
        });
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(blockedEvt)}}]""");
        JsonElement blockedResponse = await socket.ReceiveUntilAsync("OK");
        Assert.False(blockedResponse[2].GetBoolean());
        Assert.StartsWith("blocked:", blockedResponse[3].GetString());

        (NostrEvent allowedEvt, _) = SignEvent("allowed kind", kind: 1);
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(allowedEvt)}}]""");
        JsonElement allowedResponse = await socket.ReceiveUntilAsync("OK");
        Assert.True(allowedResponse[2].GetBoolean());
    }

    [Fact]
    public async Task RateLimiting_RejectsEventsPastConfiguredRate()
    {
        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Limits:EventRateLimitPerMinute"] = "1",
        });
        using WebSocket socket = await factory.ConnectAsync();

        (NostrEvent first, _) = SignEvent("first event, within budget");
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(first)}}]""");
        JsonElement firstResponse = await socket.ReceiveUntilAsync("OK");
        Assert.True(firstResponse[2].GetBoolean());

        (NostrEvent second, _) = SignEvent("second event, over budget");
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(second)}}]""");
        JsonElement secondResponse = await socket.ReceiveUntilAsync("OK");

        Assert.False(secondResponse[2].GetBoolean());
        Assert.StartsWith("rate-limited:", secondResponse[3].GetString());
    }

    [Fact]
    public async Task MaxFiltersPerSubscription_RejectsRequestWithTooManyFilters()
    {
        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Limits:MaxFiltersPerSubscription"] = "1",
        });
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync("""["REQ", "sub1", {"kinds":[1]}, {"kinds":[2]}]""");
        JsonElement response = await socket.ReceiveUntilAsync("CLOSED");

        Assert.Equal("sub1", response[1].GetString());
        Assert.Contains("too many filters", response[2].GetString());
    }

    [Fact]
    public async Task MaxConnections_RejectsConnectionPastConfiguredLimit()
    {
        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Limits:MaxConnections"] = "1",
        });

        using WebSocket first = await factory.ConnectAsync();

        // The second connection attempt should be refused before the WebSocket handshake
        // completes (a plain HTTP rejection, per Program.cs's design), which surfaces to
        // the client as a failed handshake rather than a message it can read from the socket.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using WebSocket second = await factory.ConnectAsync();
        });
    }

    [Fact]
    public async Task TimestampSanity_RejectsEventTooFarInFuture()
    {
        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Limits:CreatedAtUpperLimitSeconds"] = "60",
        });
        using WebSocket socket = await factory.ConnectAsync();

        var tooFuture = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        (NostrEvent evt, _) = SignEvent("too far future", createdAt: tooFuture);

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.False(response[2].GetBoolean());
        Assert.StartsWith("invalid:", response[3].GetString());
        Assert.Contains("future", response[3].GetString());
    }

    [Fact]
    public async Task TimestampSanity_RejectsEventTooFarInPast()
    {
        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Limits:CreatedAtLowerLimitSeconds"] = "60",
        });
        using WebSocket socket = await factory.ConnectAsync();

        var tooOld = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        (NostrEvent evt, _) = SignEvent("too far past", createdAt: tooOld);

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.False(response[2].GetBoolean());
        Assert.StartsWith("invalid:", response[3].GetString());
        Assert.Contains("past", response[3].GetString());
    }

    [Fact]
    public async Task TimestampSanity_AcceptsEventWithinConfiguredWindow()
    {
        await using var factory = new NostrRelayWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Limits:CreatedAtLowerLimitSeconds"] = "60",
            ["Limits:CreatedAtUpperLimitSeconds"] = "60",
        });
        using WebSocket socket = await factory.ConnectAsync();

        (NostrEvent evt, _) = SignEvent("right now", createdAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await socket.ReceiveUntilAsync("OK");

        Assert.True(response[2].GetBoolean());
    }
}