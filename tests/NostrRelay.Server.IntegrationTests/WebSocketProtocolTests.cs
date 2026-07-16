using System.Net.WebSockets;
using System.Text.Json;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

public class WebSocketProtocolTests : IAsyncLifetime
{
    private NostrRelayWebApplicationFactory _factory = null!;
    private WebSocket _socket = null!;

    public async Task InitializeAsync()
    {
        _factory = new NostrRelayWebApplicationFactory();
        _socket = await _factory.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        _socket.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task PublishingValidEvent_ReturnsOkTrue()
    {
        (NostrEvent evt, _) = SignEvent(content: "hello relay");

        await _socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await _socket.ReceiveArrayAsync();

        Assert.Equal("OK", response[0].GetString());
        Assert.Equal(evt.Id, response[1].GetString());
        Assert.True(response[2].GetBoolean());
    }

    [Fact]
    public async Task PublishingEventWithTamperedSignature_ReturnsOkFalseWithInvalidPrefix()
    {
        (NostrEvent evt, _) = SignEvent(content: "will be tampered");
        NostrEvent tampered = evt with { Sig = new string('0', 128) };

        await _socket.SendAsync($$"""["EVENT", {{ToEventJson(tampered)}}]""");
        JsonElement response = await _socket.ReceiveArrayAsync();

        Assert.Equal("OK", response[0].GetString());
        Assert.False(response[2].GetBoolean());
        Assert.StartsWith("invalid:", response[3].GetString());
    }

    [Fact]
    public async Task PublishingSameEventTwice_SecondReturnsOkTrueWithDuplicatePrefix()
    {
        (NostrEvent evt, _) = SignEvent(content: "dup me");
        var eventJson = ToEventJson(evt);

        await _socket.SendAsync($$"""["EVENT", {{eventJson}}]""");
        await _socket.ReceiveArrayAsync();

        await _socket.SendAsync($$"""["EVENT", {{eventJson}}]""");
        JsonElement second = await _socket.ReceiveArrayAsync();

        Assert.True(second[2].GetBoolean());
        Assert.StartsWith("duplicate:", second[3].GetString());
    }

    [Fact]
    public async Task Req_AfterPublishing_ReturnsMatchingEventThenEose()
    {
        (NostrEvent evt, var pubkeyHex) = SignEvent(content: "findable");
        await _socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        await _socket.ReceiveArrayAsync(); // OK

        await _socket.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        JsonElement eventMessage = await _socket.ReceiveArrayAsync();
        JsonElement eoseMessage = await _socket.ReceiveArrayAsync();

        Assert.Equal("EVENT", eventMessage[0].GetString());
        Assert.Equal("sub1", eventMessage[1].GetString());
        Assert.Equal(evt.Id, eventMessage[2].GetProperty("id").GetString());

        Assert.Equal("EOSE", eoseMessage[0].GetString());
        Assert.Equal("sub1", eoseMessage[1].GetString());
    }

    [Fact]
    public async Task Req_WithNoMatches_ReturnsOnlyEose()
    {
        await _socket.SendAsync("""["REQ", "sub-empty", {"authors": ["nonexistent-pubkey-00000000000000000000000000000000000000000000000000"]}]""");

        JsonElement response = await _socket.ReceiveArrayAsync();

        Assert.Equal("EOSE", response[0].GetString());
        Assert.Equal("sub-empty", response[1].GetString());
    }

    [Fact]
    public async Task Close_IsAcceptedWithoutError()
    {
        await _socket.SendAsync("""["CLOSE", "some-sub"]""");

        // Follow up with a REQ to prove the connection is still healthy afterward.
        await _socket.SendAsync("""["REQ", "sub-after-close", {}]""");
        JsonElement response = await _socket.ReceiveArrayAsync();

        Assert.Equal("EOSE", response[0].GetString());
    }

    [Fact]
    public async Task MalformedMessage_ReturnsNotice()
    {
        await _socket.SendAsync("""["UNKNOWN_TYPE", "x"]""");

        JsonElement response = await _socket.ReceiveArrayAsync();

        Assert.Equal("NOTICE", response[0].GetString());
    }
}