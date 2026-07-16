using Dapper;
using Microsoft.Data.Sqlite;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// Opens and configures a fresh <see cref="SqliteConnection"/> per operation, per the
/// spec's guidance (Section 5.2): "a small custom pool or Microsoft.Data.Sqlite
/// connection-per-operation with WAL is fine for SQLite given its concurrency model."
///
/// Two pragmas are set on every open rather than once at startup: WAL mode is persisted
/// in the database file itself (so re-setting it is a cheap no-op after the first time),
/// but <c>foreign_keys</c> is a per-connection setting in SQLite, it does not persist,
/// so it must be re-applied every time or cascading deletes (events -> event_tags)
/// silently stop working.
/// </summary>
internal static class SqliteConnectionFactory
{
    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys = ON;", cancellationToken: ct));
        return connection;
    }
}
