using System.Text.Json;

namespace NostrRelay.Core.Protocol;

/// <summary>
/// Single shared <see cref="JsonSerializerOptions"/> instance carrying the
/// <see cref="NostrEventJsonConverter"/> and <see cref="NostrFilterJsonConverter"/>.
/// Reuse this everywhere rather than constructing new options per call: STJ caches
/// reflection metadata per options instance, so a shared instance is both simpler and
/// measurably faster on the hot path (event ingestion, Section 4.1).
/// </summary>
public static class NostrJsonOptions
{
    public static readonly JsonSerializerOptions Default = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NostrEventJsonConverter());
        options.Converters.Add(new NostrFilterJsonConverter());
        return options;
    }
}
