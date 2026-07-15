using System.Text;
using System.Text.Json;

namespace NostrRelay.Core.Protocol;

/// <summary>
/// Base type for the seven relay-to-client message shapes (Section 2.2). Each subtype
/// writes its own heterogeneous array via <see cref="WriteElements"/>; <see cref="ToJson"/>
/// wraps the shared array-envelope boilerplate once so subtypes only declare their fields.
/// </summary>
public abstract record RelayMessage
{
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteElements(writer);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    protected abstract void WriteElements(Utf8JsonWriter writer);
}

/// <summary><c>["EVENT", &lt;subscription_id&gt;, &lt;event&gt;]</c> — deliver a matching event for a subscription.</summary>
public sealed record EventRelayMessage(string SubscriptionId, NostrEvent Event) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("EVENT");
        writer.WriteStringValue(SubscriptionId);
        JsonSerializer.Serialize(writer, Event, NostrJsonOptions.Default);
    }
}

/// <summary><c>["OK", &lt;event_id&gt;, &lt;true|false&gt;, &lt;message&gt;]</c> — acknowledge an EVENT publish.</summary>
public sealed record OkRelayMessage(string EventId, bool Accepted, string Message) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("OK");
        writer.WriteStringValue(EventId);
        writer.WriteBooleanValue(Accepted);
        writer.WriteStringValue(Message);
    }
}

/// <summary><c>["EOSE", &lt;subscription_id&gt;]</c> — end of stored events; subsequent EVENTs for this subscription are live.</summary>
public sealed record EoseRelayMessage(string SubscriptionId) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("EOSE");
        writer.WriteStringValue(SubscriptionId);
    }
}

/// <summary><c>["CLOSED", &lt;subscription_id&gt;, &lt;message&gt;]</c> — relay unilaterally closed a subscription.</summary>
public sealed record ClosedRelayMessage(string SubscriptionId, string Message) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("CLOSED");
        writer.WriteStringValue(SubscriptionId);
        writer.WriteStringValue(Message);
    }
}

/// <summary><c>["NOTICE", &lt;message&gt;]</c> — human-readable debugging/error message.</summary>
public sealed record NoticeRelayMessage(string Message) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("NOTICE");
        writer.WriteStringValue(Message);
    }
}

/// <summary><c>["AUTH", &lt;challenge&gt;]</c> — relay asks the client to authenticate (NIP-42).</summary>
public sealed record AuthChallengeRelayMessage(string Challenge) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("AUTH");
        writer.WriteStringValue(Challenge);
    }
}

/// <summary><c>["COUNT", &lt;subscription_id&gt;, {"count": N}]</c> — response to a COUNT request (NIP-45).</summary>
public sealed record CountRelayMessage(string SubscriptionId, long Count) : RelayMessage
{
    protected override void WriteElements(Utf8JsonWriter writer)
    {
        writer.WriteStringValue("COUNT");
        writer.WriteStringValue(SubscriptionId);
        writer.WriteStartObject();
        writer.WriteNumber("count", Count);
        writer.WriteEndObject();
    }
}
