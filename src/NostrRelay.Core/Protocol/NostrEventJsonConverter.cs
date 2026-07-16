using System.Text.Json;
using System.Text.Json.Serialization;

namespace NostrRelay.Core.Protocol;

/// <summary>
/// Maps <see cref="NostrEvent"/> to/from its wire representation: a JSON object with
/// snake_case field names (id, pubkey, created_at, kind, tags, content, sig).
///
/// Hand-written rather than relying on STJ's attribute-based object mapping (Section 5.1):
/// keeps the domain type in <c>NostrRelay.Core</c> free of serialization concerns, and
/// gives full control to fail fast with a clear <see cref="JsonException"/> on any
/// malformed field rather than silently defaulting missing/wrong-typed values.
/// </summary>
public sealed class NostrEventJsonConverter : JsonConverter<NostrEvent>
{
    public override NostrEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("event must be a JSON object");

        var id = RequireString(root, "id");
        var pubkey = RequireString(root, "pubkey");
        var createdAt = RequireInt64(root, "created_at");
        var kind = RequireInt32(root, "kind");
        var content = RequireString(root, "content");
        var sig = RequireString(root, "sig");
        var tags = RequireTags(root);

        return new NostrEvent
        {
            Id = id,
            Pubkey = pubkey,
            CreatedAt = createdAt,
            Kind = kind,
            Tags = tags,
            Content = content,
            Sig = sig,
        };
    }

    public override void Write(Utf8JsonWriter writer, NostrEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("pubkey", value.Pubkey);
        writer.WriteNumber("created_at", value.CreatedAt);
        writer.WriteNumber("kind", value.Kind);

        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (var tag in value.Tags)
        {
            writer.WriteStartArray();
            foreach (var item in tag)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteString("content", value.Content);
        writer.WriteString("sig", value.Sig);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<IReadOnlyList<string>> RequireTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out JsonElement tagsElement) || tagsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("event.tags must be an array");

        var tags = new List<IReadOnlyList<string>>();
        foreach (JsonElement tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("each event.tags entry must be an array of strings");

            var tag = new List<string>();
            foreach (JsonElement item in tagElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new JsonException("tag values must be strings");
                tag.Add(item.GetString()!);
            }

            tags.Add(tag);
        }

        return tags;
    }

    private static string RequireString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement prop) || prop.ValueKind != JsonValueKind.String)
            throw new JsonException($"event.{propertyName} must be a string");
        return prop.GetString()!;
    }

    private static long RequireInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement prop) ||
            prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetInt64(out var value))
        {
            throw new JsonException($"event.{propertyName} must be an integer");
        }

        return value;
    }

    private static int RequireInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement prop) ||
            prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetInt32(out var value))
        {
            throw new JsonException($"event.{propertyName} must be an integer");
        }

        return value;
    }
}
