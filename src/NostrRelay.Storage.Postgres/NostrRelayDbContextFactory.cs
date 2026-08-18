using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Lets `dotnet ef migrations add` / `dotnet ef database update` construct a
/// <see cref="PostgresNostrRelayDbContext"/> without a running application. Needed because
/// <see cref="PostgresEventStore"/> builds its <see cref="DbContextOptions"/> manually in
/// its own constructor instead of registering the context via
/// <c>AddDbContext</c>/<c>AddDbContextFactory</c> in the Server project's DI container,
/// EF's design-time tooling has nothing to inspect otherwise.
///
/// The connection string here only matters for schema generation (EF needs to know it's
/// targeting Postgres, and to talk to *some* database to script against); it does not have
/// to be a real, reachable connection for `migrations add` (only `database update` actually
/// connects). Override via the <c>NOSTRRELAY_POSTGRES_CONNECTION</c> environment variable
/// if you want `dotnet ef database update` to target something other than local dev.
/// </summary>
public sealed class NostrRelayDbContextFactory : IDesignTimeDbContextFactory<PostgresNostrRelayDbContext>
{
    public PostgresNostrRelayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOSTRRELAY_POSTGRES_CONNECTION")
            ?? "Host=localhost;Database=nostr_relay_dev;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<PostgresNostrRelayDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new PostgresNostrRelayDbContext(optionsBuilder.Options);
    }
}
