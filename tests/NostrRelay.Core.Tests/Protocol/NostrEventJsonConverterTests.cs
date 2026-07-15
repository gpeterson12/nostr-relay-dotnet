using System.Text.Json;
using NostrRelay.Core.Protocol;

namespace NostrRelay.Core.Tests.Protocol;

public class NostrEventJsonConverterTests
{
    private const string SampleJson = """
        {
          "id": "aa00000000000000000000000000000000000000000000000000000000000000",
          "pubkey": "bb00000000000000000000000000000000000000000000000000000000000000",
          "created_at": 1700000000,
          "kind": 1,
          "tags": [["e", "cc00"], ["p", "dd00", "wss://relay.example"]],
          "content": "hello",
          "sig": "ee000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        }
        """;

    [Fact]
    public void Deserialize_ReadsAllFieldsCorrectly()
    {
        var evt = JsonSerializer.Deserialize<NostrEvent>(SampleJson, NostrJsonOptions.Default)!;

        Assert.Equal("aa00000000000000000000000000000000000000000000000000000000000000", evt.Id);
        Assert.Equal(1700000000, evt.CreatedAt);
        Assert.Equal(1, evt.Kind);
        Assert.Equal("hello", evt.Content);
        Assert.Equal(2, evt.Tags.Count);
        Assert.Equal("e", evt.Tags[0][0]);
        Assert.Equal("wss://relay.example", evt.Tags[1][2]);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_ProducesEquivalentEvent()
    {
        var original = JsonSerializer.Deserialize<NostrEvent>(SampleJson, NostrJsonOptions.Default)!;

        var serialized = JsonSerializer.Serialize(original, NostrJsonOptions.Default);
        var roundTripped = JsonSerializer.Deserialize<NostrEvent>(serialized, NostrJsonOptions.Default)!;

        Assert.Equal(original, roundTripped);
    }

    [Theory]
    [InlineData("""{"pubkey":"bb","created_at":1,"kind":1,"tags":[],"content":"","sig":"ee"}""")] // missing id
    [InlineData("""{"id":"aa","pubkey":"bb","created_at":"not-a-number","kind":1,"tags":[],"content":"","sig":"ee"}""")] // created_at wrong type
    [InlineData("""{"id":"aa","pubkey":"bb","created_at":1,"kind":1,"tags":"not-an-array","content":"","sig":"ee"}""")] // tags wrong type
    [InlineData("""{"id":"aa","pubkey":"bb","created_at":1,"kind":1,"tags":[["e","x"]],"content":"","sig":"ee","extra":true}""")] // extra field: should still work
    public void Deserialize_MalformedShapes_EitherThrowsOrToleratesExtraFields(string json)
    {
        // The last case (extra field) is expected to succeed; the first three are
        // expected to throw. Distinguish by attempting and asserting accordingly per case.
        if (json.Contains("\"extra\""))
        {
            var evt = JsonSerializer.Deserialize<NostrEvent>(json, NostrJsonOptions.Default);
            Assert.NotNull(evt);
        }
        else
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NostrEvent>(json, NostrJsonOptions.Default));
        }
    }
}
