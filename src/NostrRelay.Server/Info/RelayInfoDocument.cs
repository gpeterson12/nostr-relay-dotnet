using System.Text.Json.Serialization;

namespace NostrRelay.Server.Info;

/// <summary>
/// NIP-11's relay information document. "Any field may be omitted" per spec, so every
/// property here is nullable, and the serializer options used to write this (see
/// <see cref="RelayInfoDocumentFactory"/>) are configured to omit nulls entirely rather
/// than emit them as JSON <c>null</c>, matching how real-world relay responses look.
/// </summary>
public sealed record RelayInfoDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("banner")]
    public string? Banner { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    /// <summary>Administrative contact pubkey (32-byte hex).</summary>
    [JsonPropertyName("pubkey")]
    public string? Pubkey { get; init; }

    /// <summary>The relay's own pubkey (32-byte hex), distinct from the administrative
    /// contact. Not used by this relay; NIP-42 auth and a relay identity key aren't
    /// implemented yet (v1.1 stretch).</summary>
    [JsonPropertyName("self")]
    public string? Self { get; init; }

    [JsonPropertyName("contact")]
    public string? Contact { get; init; }

    [JsonPropertyName("supported_nips")]
    public IReadOnlyList<int>? SupportedNips { get; init; }

    [JsonPropertyName("software")]
    public string? Software { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("terms_of_service")]
    public string? TermsOfService { get; init; }

    [JsonPropertyName("limitation")]
    public RelayLimitationDocument? Limitation { get; init; }
}

/// <summary>
/// NIP-11's "limitation" object. Deliberately sparse: only fields this relay actually
/// enforces are populated (see <see cref="RelayInfoDocumentFactory"/>), fields for
/// not-yet-implemented policy (max_event_tags, max_content_length, min_pow_difficulty,
/// created_at bounds, max_limit) are left null and therefore omitted from the response,
/// rather than asserting numbers nothing in the codebase actually checks.
/// </summary>
public sealed record RelayLimitationDocument
{
    [JsonPropertyName("max_message_length")]
    public int? MaxMessageLength { get; init; }

    [JsonPropertyName("max_subscriptions")]
    public int? MaxSubscriptions { get; init; }

    [JsonPropertyName("max_subid_length")]
    public int? MaxSubidLength { get; init; }

    [JsonPropertyName("max_limit")]
    public int? MaxLimit { get; init; }

    [JsonPropertyName("max_event_tags")]
    public int? MaxEventTags { get; init; }

    [JsonPropertyName("max_content_length")]
    public int? MaxContentLength { get; init; }

    [JsonPropertyName("min_pow_difficulty")]
    public int? MinPowDifficulty { get; init; }

    [JsonPropertyName("auth_required")]
    public bool? AuthRequired { get; init; }

    [JsonPropertyName("payment_required")]
    public bool? PaymentRequired { get; init; }

    [JsonPropertyName("restricted_writes")]
    public bool? RestrictedWrites { get; init; }

    [JsonPropertyName("created_at_lower_limit")]
    public long? CreatedAtLowerLimit { get; init; }

    [JsonPropertyName("created_at_upper_limit")]
    public long? CreatedAtUpperLimit { get; init; }

    [JsonPropertyName("default_limit")]
    public int? DefaultLimit { get; init; }
}
