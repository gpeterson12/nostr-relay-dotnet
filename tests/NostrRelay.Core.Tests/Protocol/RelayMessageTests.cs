using System.Text.Json;
using NostrRelay.Core.Protocol;

namespace NostrRelay.Core.Tests.Protocol;

public class RelayMessageTests
{
    [Fact]
    public void OkRelayMessage_WritesFourElementArray()
    {
        var json = new OkRelayMessage("event-id-1", true, "").ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(4, array.GetArrayLength());
        Assert.Equal("OK", array[0].GetString());
        Assert.Equal("event-id-1", array[1].GetString());
        Assert.True(array[2].GetBoolean());
        Assert.Equal("", array[3].GetString());
    }

    [Fact]
    public void EoseRelayMessage_WritesTwoElementArray()
    {
        var json = new EoseRelayMessage("sub1").ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal("EOSE", array[0].GetString());
        Assert.Equal("sub1", array[1].GetString());
    }

    [Fact]
    public void ClosedRelayMessage_WritesThreeElementArray()
    {
        var json = new ClosedRelayMessage("sub1", "invalid: bad filter").ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(3, array.GetArrayLength());
        Assert.Equal("CLOSED", array[0].GetString());
        Assert.Equal("sub1", array[1].GetString());
        Assert.Equal("invalid: bad filter", array[2].GetString());
    }

    [Fact]
    public void NoticeRelayMessage_WritesTwoElementArray()
    {
        var json = new NoticeRelayMessage("something went wrong").ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal("NOTICE", array[0].GetString());
        Assert.Equal("something went wrong", array[1].GetString());
    }

    [Fact]
    public void AuthChallengeRelayMessage_WritesTwoElementArray()
    {
        var json = new AuthChallengeRelayMessage("challenge-string").ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal("AUTH", array[0].GetString());
        Assert.Equal("challenge-string", array[1].GetString());
    }

    [Fact]
    public void CountRelayMessage_WritesCountAsNestedObject()
    {
        var json = new CountRelayMessage("sub1", 42).ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(3, array.GetArrayLength());
        Assert.Equal("COUNT", array[0].GetString());
        Assert.Equal("sub1", array[1].GetString());
        Assert.Equal(JsonValueKind.Object, array[2].ValueKind);
        Assert.Equal(42, array[2].GetProperty("count").GetInt64());
    }

    [Fact]
    public void EventRelayMessage_EmbedsFullEventObjectAsThirdElement()
    {
        var evt = new NostrEvent
        {
            Id = new string('a', 64),
            Pubkey = new string('b', 64),
            CreatedAt = 1700000000,
            Kind = 1,
            Tags = [],
            Content = "hi",
            Sig = new string('c', 128),
        };

        var json = new EventRelayMessage("sub1", evt).ToJson();

        JsonElement array = JsonDocument.Parse(json).RootElement;
        Assert.Equal(3, array.GetArrayLength());
        Assert.Equal("EVENT", array[0].GetString());
        Assert.Equal("sub1", array[1].GetString());
        Assert.Equal("hi", array[2].GetProperty("content").GetString());
    }

    [Fact]
    public void ToJson_ProducesRoundTrippableOutput_ThroughClientMessageParserShape()
    {
        // Sanity check that outbound OK messages, if echoed back through a naive test
        // harness, at least parse as valid JSON arrays (not that a client would ever
        // send one back, this just guards against a stray trailing comma/bracket bug).
        var json = new OkRelayMessage("id", false, "invalid: bad sig").ToJson();

        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(json).RootElement.ValueKind);
    }
}
