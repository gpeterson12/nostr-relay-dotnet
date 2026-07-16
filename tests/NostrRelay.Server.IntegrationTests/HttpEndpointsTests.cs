using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

public class HttpEndpointsTests : IAsyncLifetime
{
    private NostrRelayWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new NostrRelayWebApplicationFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private void SetNostrJsonAccept() =>
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/nostr+json"));

    [Fact]
    public async Task Root_WithNostrJsonAccept_ReturnsRelayInfoDocument()
    {
        SetNostrJsonAccept();

        HttpResponseMessage response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/nostr+json", response.Content.Headers.ContentType?.MediaType);

        JsonElement doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var supportedNips = doc.GetProperty("supported_nips").EnumerateArray().Select(e => e.GetInt32()).ToList();

        Assert.Contains(1, supportedNips);
        Assert.Contains(11, supportedNips);
    }

    [Fact]
    public async Task Root_WithNostrJsonAccept_IncludesCorsHeaders()
    {
        SetNostrJsonAccept();

        HttpResponseMessage response = await _client.GetAsync("/");

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task Root_LimitationObject_ReflectsActuallyEnforcedLimitsOnly()
    {
        SetNostrJsonAccept();

        HttpResponseMessage response = await _client.GetAsync("/");
        JsonElement limitation = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("limitation");

        Assert.Equal(65536, limitation.GetProperty("max_message_length").GetInt32());
        Assert.Equal(20, limitation.GetProperty("max_subscriptions").GetInt32());
        Assert.Equal(64, limitation.GetProperty("max_subid_length").GetInt32());
        Assert.Equal(500, limitation.GetProperty("default_limit").GetInt32());
        Assert.False(limitation.GetProperty("auth_required").GetBoolean());

        // Fields for policy this relay doesn't implement yet must be genuinely absent
        // (omitted), not present with a made-up value.
        Assert.False(limitation.TryGetProperty("max_event_tags", out _));
        Assert.False(limitation.TryGetProperty("min_pow_difficulty", out _));
        Assert.False(limitation.TryGetProperty("max_limit", out _));
    }

    [Fact]
    public async Task Root_WithoutSpecialAcceptOrWebSocketUpgrade_ReturnsFriendlyPlainText()
    {
        HttpResponseMessage response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Nostr relay", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_ReturnsOkWhenStorageIsReachable()
    {
        HttpResponseMessage response = await _client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"status\":\"ok\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusTextFormat()
    {
        HttpResponseMessage response = await _client.GetAsync("/metrics");

        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("nostr_relay_connections_active", text);
        Assert.Contains("nostr_relay_subscriptions_active", text);
        Assert.Contains("nostr_relay_events_ingested_total", text);
        Assert.Contains("nostr_relay_events_rejected_total", text);
        Assert.Contains("# TYPE", text);
    }

    [Fact]
    public async Task Metrics_ReflectsIngestedEventAfterPublish()
    {
        using WebSocket socket = await _factory.ConnectAsync();
        (NostrEvent evt, _) = SignEvent("metrics test event");

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(evt)}}]""");
        await socket.ReceiveUntilAsync("OK");

        HttpResponseMessage response = await _client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("nostr_relay_events_ingested_total 1", text);
    }

    [Fact]
    public async Task Metrics_ReflectsRejectedEventByReason()
    {
        using WebSocket socket = await _factory.ConnectAsync();
        (NostrEvent evt, _) = SignEvent("will be tampered");
        NostrEvent tampered = evt with { Sig = new string('0', 128) };

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(tampered)}}]""");
        await socket.ReceiveUntilAsync("OK");

        HttpResponseMessage response = await _client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("nostr_relay_events_rejected_total{reason=\"invalid\"} 1", text);
    }
}
