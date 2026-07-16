namespace NostrRelay.Server.Configuration;

/// <summary>Bound from the "Policy" configuration section (Section 5.6). All lists default
/// empty, meaning "no policy configured" (allow everything), not "allow nothing" — see
/// <see cref="Core.Validation.PolicyValidator"/>'s handling of an empty allowlist
/// specifically.</summary>
public sealed class RelayPolicyOptions
{
    public List<string> PubkeyAllowlist { get; set; } = [];

    public List<string> PubkeyBlocklist { get; set; } = [];

    public List<int> KindBlocklist { get; set; } = [];
}
