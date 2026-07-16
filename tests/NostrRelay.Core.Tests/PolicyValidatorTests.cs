using NostrRelay.Core.Validation;

namespace NostrRelay.Core.Tests;

public class PolicyValidatorTests
{
    private static NostrEvent MakeEvent(string pubkey = "pubkey-a", int kind = 1) => new()
    {
        Id = new string('a', 64),
        Pubkey = pubkey,
        CreatedAt = 1700000000,
        Kind = kind,
        Tags = [],
        Content = "",
        Sig = new string('b', 128),
    };

    [Fact]
    public void Validate_NoListsConfigured_AllowsAnyEvent()
    {
        var validator = new PolicyValidator([], [], []);

        ValidationResult result = validator.Validate(MakeEvent());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NonEmptyAllowlist_RejectsPubkeyNotOnList()
    {
        var validator = new PolicyValidator(["allowed-pubkey"], [], []);

        ValidationResult result = validator.Validate(MakeEvent(pubkey: "some-other-pubkey"));

        Assert.False(result.IsValid);
        Assert.Contains("blocked:", result.Reason);
        Assert.Contains("allowlist", result.Reason);
    }

    [Fact]
    public void Validate_NonEmptyAllowlist_AcceptsPubkeyOnList()
    {
        var validator = new PolicyValidator(["allowed-pubkey"], [], []);

        ValidationResult result = validator.Validate(MakeEvent(pubkey: "allowed-pubkey"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PubkeyOnBlocklist_IsRejected()
    {
        var validator = new PolicyValidator([], ["blocked-pubkey"], []);

        ValidationResult result = validator.Validate(MakeEvent(pubkey: "blocked-pubkey"));

        Assert.False(result.IsValid);
        Assert.Contains("blocked:", result.Reason);
    }

    [Fact]
    public void Validate_KindOnBlocklist_IsRejected()
    {
        var validator = new PolicyValidator([], [], [4]);

        ValidationResult result = validator.Validate(MakeEvent(kind: 4));

        Assert.False(result.IsValid);
        Assert.Contains("blocked:", result.Reason);
    }

    [Fact]
    public void Validate_KindNotOnBlocklist_IsAccepted()
    {
        var validator = new PolicyValidator([], [], [4]);

        ValidationResult result = validator.Validate(MakeEvent(kind: 1));

        Assert.True(result.IsValid);
    }
}
