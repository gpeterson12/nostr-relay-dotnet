using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NostrRelay.Server.IntegrationTests;

/// <summary>
/// Runs the real <c>Program.cs</c> host in-memory (Section 7: "spin up the server in-memory
/// (WebApplicationFactory)"), overriding <c>Storage:ConnectionString</c> so each test gets
/// an isolated database rather than sharing (or colliding on) the default <c>relay.db</c>.
/// </summary>
public sealed class NostrRelayWebApplicationFactory : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"nostr-relay-itest-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:ConnectionString", $"Data Source={DatabasePath}");
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
