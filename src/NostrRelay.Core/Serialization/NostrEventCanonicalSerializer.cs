using System.Security.Cryptography;
using System.Text;

namespace NostrRelay.Core.Serialization;

/// <summary>
/// Produces the exact byte-for-byte canonical form NIP-01 defines for event id computation:
/// <c>[0, pubkey, created_at, kind, tags, content]</c> as a whitespace-free JSON array,
/// escaping only backslash, double quote, and the \n \r \t \b \f control characters.
///
/// This is deliberately hand-rolled rather than using System.Text.Json's object serializer:
/// STJ's default encoder escapes far more than NIP-01 requires (unicode, HTML-unsafe chars,
/// etc.), which would produce a different byte sequence and therefore a different id than
/// every other relay/client implementation. Byte-exact output here is not a style choice,
/// it is a correctness requirement.
/// </summary>
public static class NostrEventCanonicalSerializer
{
    public static string Serialize(
        string pubkey,
        long createdAt,
        int kind,
        IReadOnlyList<IReadOnlyList<string>> tags,
        string content)
    {
        var sb = new StringBuilder();
        sb.Append("[0,");
        AppendJsonString(sb, pubkey);
        sb.Append(',');
        sb.Append(createdAt);
        sb.Append(',');
        sb.Append(kind);
        sb.Append(',');
        AppendTags(sb, tags);
        sb.Append(',');
        AppendJsonString(sb, content);
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Computes the lowercase-hex SHA256 id for the given field values, per NIP-01 rule:
    /// id = sha256(canonical_serialization).
    /// </summary>
    public static string ComputeId(
        string pubkey,
        long createdAt,
        int kind,
        IReadOnlyList<IReadOnlyList<string>> tags,
        string content)
    {
        var canonical = Serialize(pubkey, createdAt, kind, tags, content);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeId(NostrEvent evt) =>
        ComputeId(evt.Pubkey, evt.CreatedAt, evt.Kind, evt.Tags, evt.Content);

    private static void AppendTags(StringBuilder sb, IReadOnlyList<IReadOnlyList<string>> tags)
    {
        sb.Append('[');
        for (var i = 0; i < tags.Count; i++)
        {
            if (i > 0)
                sb.Append(',');

            sb.Append('[');
            var tag = tags[i];
            for (var j = 0; j < tag.Count; j++)
            {
                if (j > 0)
                    sb.Append(',');

                AppendJsonString(sb, tag[j]);
            }

            sb.Append(']');
        }

        sb.Append(']');
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}
