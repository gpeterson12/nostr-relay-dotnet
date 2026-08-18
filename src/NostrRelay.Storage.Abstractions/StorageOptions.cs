namespace NostrRelay.Storage.Abstractions;

/// <summary>Bound from the "Storage" configuration section (Section 5.6). <see cref="Provider"/>
/// picks which <c>IEventStore</c> implementation Program.cs registers ("Sqlite" or
/// "Postgres"); <see cref="ConnectionString"/> is that provider's connection string.
/// </summary>
public sealed class StorageOptions
{
    public string Provider { get; set; } = "Sqlite";
 
    public string? ConnectionString { get; set; }
}