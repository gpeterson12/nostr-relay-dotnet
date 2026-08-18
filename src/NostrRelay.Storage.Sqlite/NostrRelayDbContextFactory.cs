using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// Lets `dotnet ef migrations add` construct a <see cref="SqliteNostrRelayDbContext"/> without a
/// running application, same rationale as the Postgres project's equivalent factory:
/// <see cref="SqliteEventStore"/> builds its <see cref="DbContextOptions"/> manually rather
/// than through DI, so EF's design-time tooling has nothing to inspect otherwise.
///
/// The path here only matters for schema generation, not for `migrations add` to succeed;
/// override via <c>NOSTRRELAY_SQLITE_CONNECTION</c> if you want `dotnet ef database update`
/// to target something other than a local dev file.
/// </summary>
public sealed class NostrRelayDbContextFactory : IDesignTimeDbContextFactory<SqliteNostrRelayDbContext>
{
    public SqliteNostrRelayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOSTRRELAY_SQLITE_CONNECTION")
            ?? "Data Source=relay.db";

        var optionsBuilder = new DbContextOptionsBuilder<SqliteNostrRelayDbContext>();
        optionsBuilder.UseSqlite(connectionString);

        return new SqliteNostrRelayDbContext(optionsBuilder.Options);
    }
}
