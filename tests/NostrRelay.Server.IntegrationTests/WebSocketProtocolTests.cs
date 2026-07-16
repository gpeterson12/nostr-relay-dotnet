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

public class WebSocketProtocolTests : IAsyncLifetime
{
    private NostrRelayWebApplicationFactory _factory = null!;
    private WebSocket _socket = null!;

    public async Task InitializeAsync()
    {
        _factory = new NostrRelayWebApplicationFactory();
        WebSocketClient client = _factory.Server.CreateWebSocketClient();
        _socket = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/"), CancellationToken.None);
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

        await SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        JsonElement response = await ReceiveArrayAsync();

        Assert.Equal("OK", response[0].GetString());
        Assert.Equal(evt.Id, response[1].GetString());
        Assert.True(response[2].GetBoolean());
    }

    [Fact]
    public async Task PublishingEventWithTamperedSignature_ReturnsOkFalseWithInvalidPrefix()
    {
        (NostrEvent evt, _) = SignEvent(content: "will be tampered");
        NostrEvent tampered = evt with { Sig = new string('0', 128) };

        await SendAsync($$"""["EVENT", {{ToEventJson(tampered)}}]""");
        JsonElement response = await ReceiveArrayAsync();

        Assert.Equal("OK", response[0].GetString());
        Assert.False(response[2].GetBoolean());
        Assert.StartsWith("invalid:", response[3].GetString());
    }

    [Fact]
    public async Task PublishingSameEventTwice_SecondReturnsOkTrueWithDuplicatePrefix()
    {
        (NostrEvent evt, _) = SignEvent(content: "dup me");
        var eventJson = ToEventJson(evt);

        await SendAsync($$"""["EVENT", {{eventJson}}]""");
        await ReceiveArrayAsync();

        await SendAsync($$"""["EVENT", {{eventJson}}]""");
        JsonElement second = await ReceiveArrayAsync();

        Assert.True(second[2].GetBoolean());
        Assert.StartsWith("duplicate:", second[3].GetString());
    }

    [Fact]
    public async Task Req_AfterPublishing_ReturnsMatchingEventThenEose()
    {
        (NostrEvent evt, var pubkeyHex) = SignEvent(content: "findable");
        await SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        await ReceiveArrayAsync(); // OK

        await SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"]}]""");
        JsonElement eventMessage = await ReceiveArrayAsync();
        JsonElement eoseMessage = await ReceiveArrayAsync();

        Assert.Equal("EVENT", eventMessage[0].GetString());
        Assert.Equal("sub1", eventMessage[1].GetString());
        Assert.Equal(evt.Id, eventMessage[2].GetProperty("id").GetString());

        Assert.Equal("EOSE", eoseMessage[0].GetString());
        Assert.Equal("sub1", eoseMessage[1].GetString());
    }

    [Fact]
    public async Task Req_WithNoMatches_ReturnsOnlyEose()
    {
        await SendAsync("""["REQ", "sub-empty", {"authors": ["nonexistent-pubkey-00000000000000000000000000000000000000000000000000"]}]""");

        JsonElement response = await ReceiveArrayAsync();

        Assert.Equal("EOSE", response[0].GetString());
        Assert.Equal("sub-empty", response[1].GetString());
    }

    [Fact]
    public async Task Close_IsAcceptedWithoutError()
    {
        // No live subscriptions exist yet (Milestone 4), so this just confirms CLOSE
        // doesn't crash the connection or produce an unexpected response.
        await SendAsync("""["CLOSE", "some-sub"]""");

        // Follow up with a REQ to prove the connection is still healthy afterward.
        await SendAsync("""["REQ", "sub-after-close", {}]""");
        JsonElement response = await ReceiveArrayAsync();

        Assert.Equal("EOSE", response[0].GetString());
    }

    [Fact]
    public async Task MalformedMessage_ReturnsNotice()
    {
        await SendAsync("""["UNKNOWN_TYPE", "x"]""");

        JsonElement response = await ReceiveArrayAsync();

        Assert.Equal("NOTICE", response[0].GetString());
    }

    private async Task SendAsync(string json) =>
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task<JsonElement> ReceiveArrayAsync()
    {
        var buffer = new byte[16384];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(stream.ToArray());
        return JsonDocument.Parse(json).RootElement;
    }

    private static string ToEventJson(NostrEvent evt) =>
        JsonSerializer.Serialize(evt, NostrJsonOptions.Default);

    /// <summary>
    /// Builds and genuinely signs a NostrEvent, mirroring the self-contained approach
    /// used in NostrRelay.Core.Tests: no externally sourced test vectors, correctness
    /// comes from round-tripping through the same NBitcoin.Secp256k1 primitives the
    /// server's own SignatureValidator wraps.
    /// </summary>
    private static (NostrEvent Event, string PubkeyHex) SignEvent(
        string content, int kind = 1, string seed = "integration-test-seed")
    {
        var privkeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + content + Guid.NewGuid()));
        var privkey = ECPrivKey.Create(privkeyBytes);
        var pubkeyHex = Convert.ToHexStringLower(privkey.CreateXOnlyPubKey().ToBytes());

        const long createdAt = 1700000000;
        IReadOnlyList<IReadOnlyList<string>> tags = [];

        var id = NostrEventCanonicalSerializer.ComputeId(pubkeyHex, createdAt, kind, tags, content);
        var sigHex = Convert.ToHexStringLower(privkey.SignBIP340(Convert.FromHexString(id)).ToBytes());

        var evt = new NostrEvent
        {
            Id = id,
            Pubkey = pubkeyHex,
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags,
            Content = content,
            Sig = sigHex
        };

        return (evt, pubkeyHex);
    }
}
