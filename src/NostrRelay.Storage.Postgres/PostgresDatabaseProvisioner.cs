using Npgsql;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Ensures the target Postgres <i>database</i> exists, distinct from EF Core Migrations'
/// job (via <c>NostrRelayDbContext.Database.MigrateAsync</c>) of creating tables/indexes
/// within it. Unlike SQLite, where opening a connection to a nonexistent file path creates
/// it as a side effect, a Postgres database is a top-level server object: it can't be
/// created while you're connected to it, so this opens a separate connection to Postgres's
/// own always-present <c>postgres</c> maintenance database, creates the target database
/// from there if missing, and lets the caller reconnect to it normally afterward (EF's
/// migration step, right after this runs in <see cref="PostgresEventStore.InitializeAsync"/>).
///
/// Uses plain <see cref="NpgsqlCommand"/> rather than Dapper: this is the one place in the
/// project that still talks to Postgres directly (a database-level DDL operation EF has no
/// concept of), and pulling Dapper back in as a project dependency just for this one file
/// isn't worth it now that everything else has moved to EF Core.
///
/// Requires the connecting role to have <c>CREATEDB</c>. That's true by default for local
/// dev setups (Postgres.app, Docker Postgres images), but frequently isn't true for
/// managed/production Postgres (RDS, Cloud SQL, etc.), where database provisioning is
/// deliberately kept separate from application runtime privileges. If creation fails due
/// to insufficient privilege, this assumes the database was already provisioned by
/// infrastructure tooling and proceeds rather than crashing startup; if it genuinely
/// doesn't exist and can't be created, the failure surfaces naturally and clearly on the
/// caller's own subsequent connection attempt (or EF's migration step right after this)
/// instead of being masked here.
/// </summary>
public static class PostgresDatabaseProvisioner
{
    private const string MaintenanceDatabase = "postgres";

    public static async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDatabase = builder.Database
            ?? throw new InvalidOperationException("Connection string must specify a Database.");

        builder.Database = MaintenanceDatabase;

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        bool exists;
        await using (var existsCommand = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @TargetDatabase)", connection))
        {
            existsCommand.Parameters.AddWithValue("TargetDatabase", targetDatabase);
            exists = (bool)(await existsCommand.ExecuteScalarAsync(ct))!;
        }

        if (exists)
            return;

        try
        {
            // CREATE DATABASE has no IF NOT EXISTS in Postgres and can't run inside a
            // transaction block; a plain top-level command handles both correctly here
            // since Npgsql doesn't implicitly wrap a single command in a transaction.
            await using var createCommand = new NpgsqlCommand($"CREATE DATABASE \"{targetDatabase}\"", connection);
            await createCommand.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Created concurrently by another instance between our check and create; the
            // database exists either way, which is all that actually matters here.
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // No CREATEDB privilege: expected and common in managed/production Postgres.
            // Assume infrastructure already provisioned the database.
        }
    }
}