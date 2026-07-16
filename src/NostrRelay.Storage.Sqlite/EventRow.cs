using System.Text.Json;
using NostrRelay.Core;

namespace NostrRelay.Storage.Sqlite;

/// <summary>
/// Flat row shape matching the aliased columns every query in <see cref="SqliteEventStore"/>
/// selects. Dapper maps columns to properties by name, not by NIP-01 wire convention, so
/// queries explicitly alias snake_case columns (<c>created_at AS CreatedAt</c>) to match.
/// </summary>
internal sealed class EventRow
{
    public string Id { get; set; } = "";
    public string Pubkey { get; set; } = "";
    public long CreatedAt { get; set; }
    public int Kind { get; set; }
    public string Tags { get; set; } = "[]";
    public string Content { get; set; } = "";
    public string Sig { get; set; } = "";

    public NostrEvent ToNostrEvent() => new()
    {
        Id = Id,
        Pubkey = Pubkey,
        CreatedAt = CreatedAt,
        Kind = Kind,
        Tags = JsonSerializer.Deserialize<List<List<string>>>(Tags) ?? [],
        Content = Content,
        Sig = Sig,
    };
}
