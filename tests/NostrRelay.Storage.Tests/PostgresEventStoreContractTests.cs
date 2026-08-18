using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
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
///
/// Uses plain <see cref="NpgsqlCommand"/> for the schema create/drop rather than Dapper:
/// same reasoning as <see cref="PostgresDatabaseProvisioner"/>, this is database-level DDL
/// outside anything EF models, and Dapper is no longer a dependency of the Postgres
/// project this test project references.
///
/// <see cref="PostgresEventStore"/> now takes its <see cref="NpgsqlDataSource"/>,
/// <see cref="IDbContextFactory{TContext}"/>, and <see cref="StorageOptions"/> via
/// constructor injection rather than building or deriving them itself (see that class's
/// doc comment), so <see cref="CreateStoreAsync"/> builds the same pieces
/// <c>Program.cs</c>'s DI registration would, directly, without needing a full
/// <see cref="IServiceProvider"/> just to satisfy the constructor. There's no container
/// here to bind <see cref="StorageOptions"/> from configuration, so
/// <see cref="Options.Create{TOptions}"/> constructs the <see cref="IOptions{TOptions}"/>
/// wrapper directly around an instance carrying <c>isolatedConnectionString</c>, the same
/// value <see cref="PostgresEventStore"/> would otherwise get resolved from configuration
/// in the real app. Because the data source is no longer owned/disposed by
/// <see cref="PostgresEventStore"/> itself, this test owns it instead and disposes it in
/// <see cref="DisposeStoreAsync"/>.
/// </summary>
public sealed class PostgresEventStoreContractTests : EventStoreContractTests
{
    private const string DefaultConnectionString = "Host=localhost;Database=nostr_relay_test";

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("NOSTR_RELAY_TEST_POSTGRES_CONNECTION_STRING") ?? DefaultConnectionString;

    private string _schemaName = "";
    private NpgsqlDataSource? _dataSource;

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
            await using var createSchemaCommand =
                new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{_schemaName}\"", setupConnection);
            await createSchemaCommand.ExecuteNonQueryAsync();
        }

        var isolatedConnectionString = $"{BaseConnectionString};SearchPath={_schemaName}";

        _dataSource = NpgsqlDataSource.Create(isolatedConnectionString);

        var optionsBuilder = new DbContextOptionsBuilder<PostgresNostrRelayDbContext>();
        optionsBuilder.UseNpgsql(_dataSource, npgsqlOptions => npgsqlOptions.EnableRetryOnFailure());
        var contextFactory = new PooledDbContextFactory<PostgresNostrRelayDbContext>(optionsBuilder.Options);

        var storageOptions = Options.Create(new StorageOptions
        {
            Provider = "Postgres",
            ConnectionString = isolatedConnectionString,
        });

        var store = new PostgresEventStore(contextFactory, storageOptions);
        await store.InitializeAsync();
        return store;
    }

    protected override async Task DisposeStoreAsync()
    {
        // Dispose the data source before dropping the schema, so any pooled connections
        // it's still holding are released rather than potentially holding a lock that
        // would make the DROP SCHEMA below block or fail.
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();

        await using var connection = new NpgsqlConnection(BaseConnectionString);
        await connection.OpenAsync();
        await using var dropSchemaCommand =
            new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE", connection);
        await dropSchemaCommand.ExecuteNonQueryAsync();
    }
}