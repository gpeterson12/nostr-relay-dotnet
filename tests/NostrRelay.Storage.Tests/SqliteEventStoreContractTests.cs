using NostrRelay.Storage.Abstractions;
using NostrRelay.Storage.Sqlite;

namespace NostrRelay.Storage.Tests;

/// <summary>
/// Runs the full <see cref="EventStoreContractTests"/> suite against <see cref="SqliteEventStore"/>.
/// Each test gets its own temp-file database (xUnit creates a new class instance per test
/// method by default, so <see cref="CreateStoreAsync"/> runs fresh every time) rather than
/// an in-memory SQLite connection, since Microsoft.Data.Sqlite's in-memory mode requires
/// keeping a single connection open for the database's lifetime, which conflicts with this
/// store's connection-per-operation design.
/// </summary>
public sealed class SqliteEventStoreContractTests : EventStoreContractTests
{
    private string _dbPath = "";

    protected override async Task<IEventStore> CreateStoreAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"nostr-relay-tests-{Guid.NewGuid():N}.db");
        var store = new SqliteEventStore($"Data Source={_dbPath}");
        await store.InitializeAsync();
        return store;
    }

    protected override Task DisposeStoreAsync()
    {
        // SQLite may leave -wal/-shm sidecar files alongside the main db in WAL mode;
        // clean up all three so temp-file tests don't leak disk space across runs.
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
