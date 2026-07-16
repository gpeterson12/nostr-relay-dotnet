using NostrRelay.Core.Validation;

namespace NostrRelay.Core.Tests;

public class PolicyValidatorTests
{
    // Generous enough that no test in this file concerned with pubkey/kind policy needs
    // to think about the timestamp checks at all.
    private const long WideOpenTimestampWindow = 100L * 365 * 24 * 60 * 60; // ~100 years

    private static NostrEvent MakeEvent(string pubkey = "pubkey-a", int kind = 1, long? createdAt = null) => new()
    {
        Id = new string('a', 64),
        Pubkey = pubkey,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Kind = kind,
        Tags = [],
        Content = "",
        Sig = new string('b', 128),
    };

    private static PolicyValidator MakeValidator(
        IReadOnlyCollection<string>? pubkeyAllowlist = null,
        IReadOnlyCollection<string>? pubkeyBlocklist = null,
        IReadOnlyCollection<int>? kindBlocklist = null,
        long createdAtLowerLimitSeconds = WideOpenTimestampWindow,
        long createdAtUpperLimitSeconds = WideOpenTimestampWindow) =>
        new(pubkeyAllowlist ?? [], pubkeyBlocklist ?? [], kindBlocklist ?? [], createdAtLowerLimitSeconds, createdAtUpperLimitSeconds);

    [Fact]
    public void Validate_NoListsConfigured_AllowsAnyEvent()
    {
        PolicyValidator validator = MakeValidator();

        ValidationResult result = validator.Validate(MakeEvent());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NonEmptyAllowlist_RejectsPubkeyNotOnList()
    {
        PolicyValidator validator = MakeValidator(pubkeyAllowlist: ["allowed-pubkey"]);

        ValidationResult result = validator.Validate(MakeEvent(pubkey: "some-other-pubkey"));

        Assert.False(result.IsValid);
        Assert.Contains("blocked:", result.Reason);
        Assert.Contains("allowlist", result.Reason);
    }

    [Fact]
    public void Validate_NonEmptyAllowlist_AcceptsPubkeyOnList()
    {
        PolicyValidator validator = MakeValidator(pubkeyAllowlist: ["allowed-pubkey"]);

        ValidationResult result = validator.Validate(MakeEvent(pubkey: "allowed-pubkey"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PubkeyOnBlocklist_IsRejected()
    {
        PolicyValidator validator = MakeValidator(pubkeyBlocklist: ["blocked-pubkey"]);

        ValidationResult result = validator.Validate(MakeEvent(pubkey: "blocked-pubkey"));

        Assert.False(result.IsValid);
        Assert.Contains("blocked:", result.Reason);
    }

    [Fact]
    public void Validate_KindOnBlocklist_IsRejected()
    {
        PolicyValidator validator = MakeValidator(kindBlocklist: [4]);

        ValidationResult result = validator.Validate(MakeEvent(kind: 4));

        Assert.False(result.IsValid);
        Assert.Contains("blocked:", result.Reason);
    }

    [Fact]
    public void Validate_KindNotOnBlocklist_IsAccepted()
    {
        PolicyValidator validator = MakeValidator(kindBlocklist: [4]);

        ValidationResult result = validator.Validate(MakeEvent(kind: 1));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_CreatedAtWithinWindow_IsAccepted()
    {
        PolicyValidator validator = MakeValidator(createdAtLowerLimitSeconds: 3600, createdAtUpperLimitSeconds: 300);

        ValidationResult result = validator.Validate(MakeEvent(createdAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_CreatedAtTooFarInPast_IsRejected()
    {
        PolicyValidator validator = MakeValidator(createdAtLowerLimitSeconds: 3600, createdAtUpperLimitSeconds: 300);
        var tooOld = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();

        ValidationResult result = validator.Validate(MakeEvent(createdAt: tooOld));

        Assert.False(result.IsValid);
        Assert.Contains("invalid:", result.Reason);
        Assert.Contains("past", result.Reason);
    }

    [Fact]
    public void Validate_CreatedAtTooFarInFuture_IsRejected()
    {
        PolicyValidator validator = MakeValidator(createdAtLowerLimitSeconds: 3600, createdAtUpperLimitSeconds: 300);
        var tooFuture = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        ValidationResult result = validator.Validate(MakeEvent(createdAt: tooFuture));

        Assert.False(result.IsValid);
        Assert.Contains("invalid:", result.Reason);
        Assert.Contains("future", result.Reason);
    }

    [Fact]
    public void Validate_CreatedAtExactlyAtLowerBoundary_IsAccepted()
    {
        PolicyValidator validator = MakeValidator(createdAtLowerLimitSeconds: 3600, createdAtUpperLimitSeconds: 300);
        var atBoundary = DateTimeOffset.UtcNow.AddSeconds(-3600).ToUnixTimeSeconds();

        ValidationResult result = validator.Validate(MakeEvent(createdAt: atBoundary));

        Assert.True(result.IsValid);
    }
}