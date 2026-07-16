using System.Text.Json;
using NostrRelay.Core;

namespace NostrRelay.Storage.Postgres;

/// <summary>
/// Flat row shape for query results. Every query selects <c>tags::text</c> rather than
/// the raw JSONB column, deliberately avoiding any dependency on Npgsql's dynamic JSON
/// type mapping (which varies across versions); a plain string plus
/// <see cref="JsonSerializer"/> is simpler and version-independent.
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
        Id = Id.TrimEnd(), // CHAR(n) columns space-pad short values; our values are always exact-length, but trim defensively
        Pubkey = Pubkey.TrimEnd(),
        CreatedAt = CreatedAt,
        Kind = Kind,
        Tags = JsonSerializer.Deserialize<List<List<string>>>(Tags) ?? [],
        Content = Content,
        Sig = Sig.TrimEnd(),
    };
}
