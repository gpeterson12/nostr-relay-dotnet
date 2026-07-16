using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// Applies the schema from the numbered <c>.sql</c> files under <c>Schema/</c>, embedded
/// as resources at build time. Kept as standalone <c>.sql</c> files rather than C# string
/// constants for two reasons: IDE tooling (Rider's DDL data source, syntax highlighting,
/// SQL inspections) can introspect real <c>.sql</c> files but not string literals, and the
/// numbered filenames double as a lightweight migration ledger as the schema evolves.
///
/// Each file's statements are idempotent (<c>IF NOT EXISTS</c>), so re-running the full
/// set on every startup is safe; this is not a "run once, track applied migrations" system,
/// just ordered, repeatable DDL. A real migration tracking table is a reasonable future
/// addition once the schema needs actual ALTERs instead of only CREATE IF NOT EXISTS.
/// </summary>
internal static class SqliteSchema
{
    // Order matters: event_tags' foreign key references events, and index creation
    // assumes both tables already exist.
    private static readonly string[] MigrationResourceNames =
    [
        "NostrRelay.Storage.Sqlite.Schema.001_create_events_table.sql",
        "NostrRelay.Storage.Sqlite.Schema.002_create_event_tags_table.sql",
        "NostrRelay.Storage.Sqlite.Schema.003_create_indexes.sql",
    ];

    public static async Task EnsureCreatedAsync(SqliteConnection connection, CancellationToken ct)
    {
        foreach (var resourceName in MigrationResourceNames)
        {
            var script = ReadEmbeddedSql(resourceName);

            // Executed statement-by-statement rather than as one multi-statement command:
            // ADO.NET providers vary in whether a single ExecuteNonQuery call runs every
            // semicolon-separated statement in a script or only the first, so splitting
            // here avoids depending on that behavior.
            foreach (var statement in SplitStatements(script))
                await connection.ExecuteAsync(new CommandDefinition(statement, cancellationToken: ct));
        }
    }

    private static string ReadEmbeddedSql(string resourceName)
    {
        Assembly assembly = typeof(SqliteSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
                              ?? throw new InvalidOperationException(
                                  $"Embedded schema resource '{resourceName}' not found. Confirm the .csproj includes " +
                                  "<EmbeddedResource Include=\"Schema\\*.sql\" /> and the file exists under Schema/.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Splits a script into individual statements. Strips "--" line comments first,
    /// deliberately, not incidentally: a naive split on ";" alone breaks the moment a
    /// comment's prose happens to contain a semicolon (found the hard way in
    /// PostgresSchema's equivalent method: an explanatory comment mentioning "...this
    /// codebase uses; this GIN index..." was enough to chop a sentence in half and hand
    /// the database a fragment starting mid-comment as if it were real SQL). This is a
    /// line-based strip, not a real SQL tokenizer, it would be fooled by "--" appearing
    /// inside a string literal; safe here because this project's own DDL never puts "--"
    /// inside a string, not safe in general for arbitrary SQL.
    /// </summary>
    private static IEnumerable<string> SplitStatements(string script)
    {
        var withoutComments = string.Join('\n', script.Split('\n').Select(StripLineComment));

        return withoutComments
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => statement.Length > 0);
    }

    private static string StripLineComment(string line)
    {
        var commentIndex = line.IndexOf("--", StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }
}