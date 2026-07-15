using System.Text.Json;
using System.Text.Json.Serialization;

namespace NostrRelay.Core.Protocol;

/// <summary>
/// Maps <see cref="NostrFilter"/> to/from its wire representation (Section 3.4). The
/// dynamic single-letter tag keys (<c>#e</c>, <c>#p</c>, etc.) are why this can't be a
/// standard attribute-mapped object: the property name itself varies per filter, so it
/// has to be discovered by enumerating object properties rather than declared up front.
/// </summary>
public sealed class NostrFilterJsonConverter : JsonConverter<NostrFilter>
{
    public override NostrFilter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("filter must be a JSON object");

        IReadOnlyList<string>? ids = null;
        IReadOnlyList<string>? authors = null;
        IReadOnlyList<int>? kinds = null;
        long? since = null;
        long? until = null;
        int? limit = null;
        Dictionary<char, IReadOnlyList<string>>? tagFilters = null;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "ids":
                    ids = ReadStringArray(property.Value, "ids");
                    break;
                case "authors":
                    authors = ReadStringArray(property.Value, "authors");
                    break;
                case "kinds":
                    kinds = ReadIntArray(property.Value);
                    break;
                case "since":
                    since = RequireInt64(property.Value, "since");
                    break;
                case "until":
                    until = RequireInt64(property.Value, "until");
                    break;
                case "limit":
                    limit = RequireInt32(property.Value, "limit");
                    break;
                default:
                    if (property.Name.Length == 2 && property.Name[0] == '#')
                    {
                        tagFilters ??= new Dictionary<char, IReadOnlyList<string>>();
                        tagFilters[property.Name[1]] = ReadStringArray(property.Value, property.Name);
                    }
                    // Unrecognized properties beyond the "#<letter>" convention are ignored
                    // rather than rejected, per NIP-01's forward-compatibility expectations.
                    break;
            }
        }

        return new NostrFilter
        {
            Ids = ids,
            Authors = authors,
            Kinds = kinds,
            TagFilters = tagFilters,
            Since = since,
            Until = until,
            Limit = limit,
        };
    }

    public override void Write(Utf8JsonWriter writer, NostrFilter value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.Ids is { Count: > 0 })
            WriteStringArray(writer, "ids", value.Ids);

        if (value.Authors is { Count: > 0 })
            WriteStringArray(writer, "authors", value.Authors);

        if (value.Kinds is { Count: > 0 })
        {
            writer.WritePropertyName("kinds");
            writer.WriteStartArray();
            foreach (var kind in value.Kinds)
                writer.WriteNumberValue(kind);
            writer.WriteEndArray();
        }

        if (value.TagFilters is { Count: > 0 })
        {
            foreach (var (tagName, values) in value.TagFilters)
                WriteStringArray(writer, $"#{tagName}", values);
        }

        if (value.Since is { } since)
            writer.WriteNumber("since", since);

        if (value.Until is { } until)
            writer.WriteNumber("until", until);

        if (value.Limit is { } limit)
            writer.WriteNumber("limit", limit);

        writer.WriteEndObject();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException($"filter.{propertyName} must be an array of strings");

        var list = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonException($"filter.{propertyName} entries must be strings");
            list.Add(item.GetString()!);
        }

        return list;
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException("filter.kinds must be an array of integers");

        var list = new List<int>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var kind))
                throw new JsonException("filter.kinds entries must be integers");
            list.Add(kind);
        }

        return list;
    }

    private static long RequireInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw new JsonException($"filter.{propertyName} must be an integer");
        return value;
    }

    private static int RequireInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new JsonException($"filter.{propertyName} must be an integer");
        return value;
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
