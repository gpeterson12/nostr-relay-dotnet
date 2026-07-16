using System.Reflection;
using Dapper;
using Npgsql;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Postgres variant of the embedded-.sql-migration pattern established in
/// NostrRelay.Storage.Sqlite: numbered files under Schema/, embedded as resources,
/// applied in order. See SqliteSchema for the full rationale (Rider DDL introspection,
/// numbered files as a lightweight migration ledger); it applies identically here.
/// </summary>
internal static class PostgresSchema
{
    private static readonly string[] MigrationResourceNames =
    [
        "NostrRelay.Storage.Postgres.Schema.001_create_events_table.sql",
        "NostrRelay.Storage.Postgres.Schema.002_create_event_tags_table.sql",
        "NostrRelay.Storage.Postgres.Schema.003_create_indexes.sql",
        "NostrRelay.Storage.Postgres.Schema.004_create_unique_indexes.sql",
    ];

    public static async Task EnsureCreatedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        foreach (var resourceName in MigrationResourceNames)
        {
            var script = ReadEmbeddedSql(resourceName);

            foreach (var statement in SplitStatements(script))
                await connection.ExecuteAsync(new CommandDefinition(statement, cancellationToken: ct));
        }
    }

    private static string ReadEmbeddedSql(string resourceName)
    {
        Assembly assembly = typeof(PostgresSchema).Assembly;
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
    /// comment's prose happens to contain a semicolon (found the hard way: an explanatory
    /// comment mentioning "...this codebase uses; this GIN index..." was enough to chop a
    /// sentence in half and hand Postgres a fragment starting mid-comment as if it were
    /// real SQL). This is a line-based strip, not a real SQL tokenizer, it would be
    /// fooled by "--" appearing inside a string literal; safe here because this project's
    /// own DDL never puts "--" inside a string, not safe in general for arbitrary SQL.
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