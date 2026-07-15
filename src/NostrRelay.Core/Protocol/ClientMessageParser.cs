using System.Text.Json;

namespace NostrRelay.Core.Protocol;

/// <summary>
/// Parses a raw WebSocket text frame into a <see cref="ClientMessage"/>. This is the
/// "hand-written converter for the outer message envelope" the spec calls for (Section
/// 5.1): the envelope is a heterogeneous JSON array whose shape depends on element 0, a
/// case standard STJ object-mapping doesn't handle. Rather than writing a single generic
/// array converter, this dispatches by message type and delegates each element's
/// deserialization to <see cref="NostrJsonOptions.Default"/>, which is more debuggable
/// and keeps each message shape's parsing logic in one readable place.
///
/// Every failure mode surfaces as <see cref="NostrProtocolException"/> so the server
/// layer has one exception type to catch at the connection loop boundary.
/// </summary>
public static class ClientMessageParser
{
    public static ClientMessage Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new NostrProtocolException("message is not valid JSON", ex);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                throw new NostrProtocolException("message must be a non-empty JSON array");

            if (root[0].ValueKind != JsonValueKind.String)
                throw new NostrProtocolException("message type (index 0) must be a string");

            var messageType = root[0].GetString()!;

            return messageType switch
            {
                "EVENT" => ParseEvent(root),
                "REQ" => ParseReq(root),
                "CLOSE" => ParseClose(root),
                "AUTH" => ParseAuth(root),
                "COUNT" => ParseCount(root),
                _ => throw new NostrProtocolException($"unknown message type: {messageType}")
            };
        }
    }

    private static EventClientMessage ParseEvent(JsonElement root)
    {
        if (root.GetArrayLength() != 2)
            throw new NostrProtocolException("EVENT must have exactly 2 elements: [\"EVENT\", <event>]");

        return new EventClientMessage(Deserialize<NostrEvent>(root[1], "event"));
    }

    private static ReqClientMessage ParseReq(JsonElement root)
    {
        if (root.GetArrayLength() < 3)
            throw new NostrProtocolException("REQ must have a subscription id and at least one filter");

        var subscriptionId = RequireSubscriptionId(root[1]);

        var filters = new List<NostrFilter>();
        for (var i = 2; i < root.GetArrayLength(); i++)
            filters.Add(Deserialize<NostrFilter>(root[i], "filter"));

        return new ReqClientMessage(subscriptionId, filters);
    }

    private static CloseClientMessage ParseClose(JsonElement root)
    {
        if (root.GetArrayLength() != 2)
            throw new NostrProtocolException("CLOSE must have exactly 2 elements: [\"CLOSE\", <subscription_id>]");

        return new CloseClientMessage(RequireSubscriptionId(root[1]));
    }

    private static AuthClientMessage ParseAuth(JsonElement root)
    {
        if (root.GetArrayLength() != 2)
            throw new NostrProtocolException("AUTH must have exactly 2 elements: [\"AUTH\", <event>]");

        return new AuthClientMessage(Deserialize<NostrEvent>(root[1], "event"));
    }

    private static CountClientMessage ParseCount(JsonElement root)
    {
        if (root.GetArrayLength() != 3)
            throw new NostrProtocolException("COUNT must have exactly 3 elements: [\"COUNT\", <subscription_id>, <filter>]");

        var subscriptionId = RequireSubscriptionId(root[1]);
        var filter = Deserialize<NostrFilter>(root[2], "filter");
        return new CountClientMessage(subscriptionId, filter);
    }

    private static string RequireString(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new NostrProtocolException($"{fieldName} must be a string");

        return element.GetString()!;
    }

    /// <summary>
    /// NIP-01: "&lt;subscription_id&gt; is an arbitrary, non-empty string of max length 64 chars."
    /// </summary>
    private const int MaxSubscriptionIdLength = 64;

    private static string RequireSubscriptionId(JsonElement element)
    {
        var subscriptionId = RequireString(element, "subscription_id");

        if (subscriptionId.Length is 0 or > MaxSubscriptionIdLength)
            throw new NostrProtocolException("subscription_id must be non-empty and at most 64 characters");

        return subscriptionId;
    }

    private static T Deserialize<T>(JsonElement element, string fieldName)
    {
        try
        {
            return element.Deserialize<T>(NostrJsonOptions.Default)
                   ?? throw new NostrProtocolException($"{fieldName} could not be parsed");
        }
        catch (JsonException ex)
        {
            throw new NostrProtocolException($"{fieldName} is malformed: {ex.Message}", ex);
        }
    }
}