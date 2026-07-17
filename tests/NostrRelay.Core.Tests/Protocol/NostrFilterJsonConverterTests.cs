using System.Text.Json;
using NostrRelay.Core.Protocol;

namespace NostrRelay.Core.Tests.Protocol;

public class NostrFilterJsonConverterTests
{
    [Fact]
    public void Deserialize_ParsesStandardFields()
    {
        const string json = """{"ids":["ab"],"authors":["cd"],"kinds":[1,3],"since":100,"until":200,"limit":50}""";

        var filter = JsonSerializer.Deserialize<NostrFilter>(json, NostrJsonOptions.Default)!;

        Assert.Equal(["ab"], filter.Ids);
        Assert.Equal(["cd"], filter.Authors);
        Assert.Equal([1, 3], filter.Kinds);
        Assert.Equal(100, filter.Since);
        Assert.Equal(200, filter.Until);
        Assert.Equal(50, filter.Limit);
    }

    [Fact]
    public void Deserialize_ParsesDynamicTagFilterKeys()
    {
        const string json = """{"#e":["event-id-1","event-id-2"],"#p":["pubkey-1"]}""";

        var filter = JsonSerializer.Deserialize<NostrFilter>(json, NostrJsonOptions.Default)!;

        Assert.NotNull(filter.TagFilters);
        Assert.Equal(["event-id-1", "event-id-2"], filter.TagFilters!['e']);
        Assert.Equal(["pubkey-1"], filter.TagFilters['p']);
    }

    [Fact]
    public void Deserialize_IgnoresUnrecognizedProperties()
    {
        const string json = """{"ids":["ab"],"some_future_field":"whatever"}""";

        var filter = JsonSerializer.Deserialize<NostrFilter>(json, NostrJsonOptions.Default)!;

        Assert.Equal(["ab"], filter.Ids);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesTagFilters()
    {
        var original = new NostrFilter
        {
            Kinds = [1],
            TagFilters = new Dictionary<char, IReadOnlyList<string>> { ['e'] = ["abc"], ['p'] = ["def", "ghi"] },
            Limit = 10,
        };

        var json = JsonSerializer.Serialize(original, NostrJsonOptions.Default);
        var roundTripped = JsonSerializer.Deserialize<NostrFilter>(json, NostrJsonOptions.Default)!;

        Assert.Equal(original.Kinds, roundTripped.Kinds);
        Assert.Equal(original.Limit, roundTripped.Limit);
        Assert.Equal(original.TagFilters!['e'], roundTripped.TagFilters!['e']);
        Assert.Equal(original.TagFilters['p'], roundTripped.TagFilters['p']);
    }

    [Fact]
    public void Serialize_OmitsAbsentFields()
    {
        var filter = new NostrFilter { Kinds = [1] };

        var json = JsonSerializer.Serialize(filter, NostrJsonOptions.Default);

        Assert.DoesNotContain("ids", json);
        Assert.DoesNotContain("authors", json);
        Assert.DoesNotContain("since", json);
        Assert.Contains("kinds", json);
    }

    [Fact]
    public void Deserialize_ThrowsWhenKindsContainsNonInteger()
    {
        const string json = """{"kinds":["not-a-number"]}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NostrFilter>(json, NostrJsonOptions.Default));
    }
}
