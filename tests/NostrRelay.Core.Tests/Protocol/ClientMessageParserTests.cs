using NostrRelay.Core.Protocol;

namespace NostrRelay.Core.Tests.Protocol;

public class ClientMessageParserTests
{
    private const string SampleEventJson = """
        {
          "id": "aa00000000000000000000000000000000000000000000000000000000000000",
          "pubkey": "bb00000000000000000000000000000000000000000000000000000000000000",
          "created_at": 1700000000,
          "kind": 1,
          "tags": [],
          "content": "hello",
          "sig": "ee000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        }
        """;

    [Fact]
    public void Parse_Event_ReturnsEventClientMessage()
    {
        ClientMessage message = ClientMessageParser.Parse($$"""["EVENT", {{SampleEventJson}}]""");

        var eventMessage = Assert.IsType<EventClientMessage>(message);
        Assert.Equal("hello", eventMessage.Event.Content);
    }

    [Fact]
    public void Parse_Req_ReturnsReqClientMessageWithAllFilters()
    {
        ClientMessage message = ClientMessageParser.Parse("""["REQ", "sub1", {"kinds":[1]}, {"kinds":[3]}]""");

        var req = Assert.IsType<ReqClientMessage>(message);
        Assert.Equal("sub1", req.SubscriptionId);
        Assert.Equal(2, req.Filters.Count);
        Assert.Equal([1], req.Filters[0].Kinds);
        Assert.Equal([3], req.Filters[1].Kinds);
    }

    [Fact]
    public void Parse_Close_ReturnsCloseClientMessage()
    {
        ClientMessage message = ClientMessageParser.Parse("""["CLOSE", "sub1"]""");

        var close = Assert.IsType<CloseClientMessage>(message);
        Assert.Equal("sub1", close.SubscriptionId);
    }

    [Fact]
    public void Parse_Auth_ReturnsAuthClientMessage()
    {
        ClientMessage message = ClientMessageParser.Parse($$"""["AUTH", {{SampleEventJson}}]""");

        Assert.IsType<AuthClientMessage>(message);
    }

    [Fact]
    public void Parse_Count_ReturnsCountClientMessage()
    {
        ClientMessage message = ClientMessageParser.Parse("""["COUNT", "sub1", {"kinds":[1]}]""");

        var count = Assert.IsType<CountClientMessage>(message);
        Assert.Equal("sub1", count.SubscriptionId);
        Assert.Equal([1], count.Filter.Kinds);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")] // object, not array
    [InlineData("[]")] // empty array
    [InlineData("[123]")] // type is not a string
    [InlineData("""["UNKNOWN_TYPE", "x"]""")]
    [InlineData("""["EVENT"]""")] // wrong arity
    [InlineData("""["EVENT", {}, {}]""")] // too many elements
    [InlineData("""["REQ", "sub1"]""")] // no filters
    [InlineData("""["CLOSE"]""")] // missing subscription id
    [InlineData("""["COUNT", "sub1"]""")] // missing filter
    public void Parse_MalformedInput_ThrowsNostrProtocolException(string json)
    {
        Assert.Throws<NostrProtocolException>(() => ClientMessageParser.Parse(json));
    }

    [Fact]
    public void Parse_EventWithMalformedNestedEvent_ThrowsNostrProtocolException()
    {
        var ex = Assert.Throws<NostrProtocolException>(
            () => ClientMessageParser.Parse("""["EVENT", {"id": "not-enough-fields"}]"""));

        Assert.Contains("event", ex.Message);
    }
}
