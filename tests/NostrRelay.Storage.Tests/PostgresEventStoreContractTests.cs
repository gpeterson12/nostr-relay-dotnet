using Dapper;
using Npgsql;
using NostrRelay.Storage.Abstractions;
using NostrRelay.Storage.Postgres;

namespace NostrRelay.Storage.Tests;

/// <summary>
/// Runs the full <see cref="EventStoreContractTests"/> suite against
/// <see cref="PostgresEventStore"/>, proving behavioral parity with
/// <see cref="SqliteEventStoreContractTests"/> by construction, same base class, same
/// assertions, different provider underneath.
///
/// Requires a real, reachable Postgres server. Defaults to
/// <c>Host=localhost;Database=nostr_relay_test</c> (Postgres.app's local trust-auth
/// defaults on macOS: no password, role matches your OS username), overridable via the
/// <c>NOSTR_RELAY_TEST_POSTGRES_CONNECTION_STRING</c> environment variable for CI or a
/// differently-configured local setup. Create the database once with:
/// <c>createdb nostr_relay_test</c>
///
/// Each test gets its own dedicated Postgres schema (namespace), created before the test
/// and dropped after, mirroring how the SQLite contract tests get a fresh temp-file
/// database per test, without a real Testcontainers/Docker dependency for local runs.
/// Section 7 calls out Testcontainers as the CI-time approach for Postgres, a reasonable
/// upgrade once this project has real CI, this connects directly to a local server instead.
/// </summary>
public sealed class PostgresEventStoreContractTests : EventStoreContractTests
{
    private const string DefaultConnectionString = "Host=localhost;Database=nostr_relay_test";

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("NOSTR_RELAY_TEST_POSTGRES_CONNECTION_STRING") ?? DefaultConnectionString;

    private string _schemaName = "";

    protected override async Task<IEventStore> CreateStoreAsync()
    {
        // Must run before the setupConnection below: that connection targets
        // nostr_relay_test directly, and would itself fail with "database does not
        // exist" if this hadn't created it yet. PostgresEventStore.InitializeAsync also
        // calls this later in this same method, that's fine, a database that already
        // exists makes the check a single cheap no-op SELECT.
        await PostgresDatabaseProvisioner.EnsureDatabaseExistsAsync(BaseConnectionString, CancellationToken.None);

        _schemaName = $"test_{Guid.NewGuid():N}";

        await using (var setupConnection = new NpgsqlConnection(BaseConnectionString))
        {
            await setupConnection.OpenAsync();
            await setupConnection.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{_schemaName}\"");
        }

        var isolatedConnectionString = $"{BaseConnectionString};SearchPath={_schemaName}";
        var store = new PostgresEventStore(isolatedConnectionString);
        await store.InitializeAsync();
        return store;
    }

    protected override async Task DisposeStoreAsync()
    {
        await using var connection = new NpgsqlConnection(BaseConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
    }
}