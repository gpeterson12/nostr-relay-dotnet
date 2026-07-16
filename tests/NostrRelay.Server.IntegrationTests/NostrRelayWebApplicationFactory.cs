using System.Net.WebSockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace NostrRelay.Server.IntegrationTests;

/// <summary>
/// Runs the real <c>Program.cs</c> host in-memory (Section 7: "spin up the server in-memory
/// (WebApplicationFactory)"), overriding <c>Storage:ConnectionString</c> so each test gets
/// an isolated database rather than sharing (or colliding on) the default <c>relay.db</c>.
/// Accepts additional arbitrary settings so Milestone 8 policy/limits tests can configure
/// tiny thresholds (e.g. a 2-connection cap, a 1-event-per-minute rate limit) rather than
/// waiting on production-sized defaults.
/// </summary>
public sealed class NostrRelayWebApplicationFactory(IReadOnlyDictionary<string, string?>? additionalSettings = null)
    : WebApplicationFactory<Program>
{
    private string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"nostr-relay-itest-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:ConnectionString", $"Data Source={DatabasePath}");

        if (additionalSettings is null)
            return;

        foreach (var (key, value) in additionalSettings)
            builder.UseSetting(key, value);
    }

    /// <summary>Opens a new WebSocket connection to the in-memory server. Tests exercising
    /// live fan-out or kind-strategy supersession typically need several of these at once
    /// (a publisher and one or more subscribers all sharing the same underlying DI
    /// singletons, since they're all the same factory instance).</summary>
    public async Task<WebSocket> ConnectAsync()
    {
        WebSocketClient client = Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(Server.BaseAddress, "/"), CancellationToken.None);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var path in new[] { DatabasePath, $"{DatabasePath}-wal", $"{DatabasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}