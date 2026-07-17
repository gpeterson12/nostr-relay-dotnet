using NostrRelay.Core.Validation;

namespace NostrRelay.Core.Tests;

public class ExpirationValidatorTests
{
    private readonly ExpirationValidator _validator = new();

    private static NostrEvent MakeEvent(IReadOnlyList<IReadOnlyList<string>>? tags = null) => new()
    {
        Id = new string('a', 64),
        Pubkey = new string('b', 64),
        CreatedAt = 1700000000,
        Kind = 1,
        Tags = tags ?? [],
        Content = "",
        Sig = new string('c', 128),
    };

    [Fact]
    public void Validate_NoExpirationTag_IsAccepted()
    {
        ValidationResult result = _validator.Validate(MakeEvent());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ExpirationInFuture_IsAccepted()
    {
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString();
        NostrEvent evt = MakeEvent([["expiration", future]]);

        ValidationResult result = _validator.Validate(evt);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ExpirationInPast_IsRejected()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();
        NostrEvent evt = MakeEvent([["expiration", past]]);

        ValidationResult result = _validator.Validate(evt);

        Assert.False(result.IsValid);
        Assert.Contains("invalid:", result.Reason);
        Assert.Contains("expired", result.Reason);
    }

    [Fact]
    public void Validate_ExpirationExactlyNow_IsRejected()
    {
        // "SHOULD be considered expired... at the specified timestamp" - the boundary
        // itself counts as expired, not still-valid.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        NostrEvent evt = MakeEvent([["expiration", now]]);

        ValidationResult result = _validator.Validate(evt);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NonNumericExpirationTag_IsRejected()
    {
        NostrEvent evt = MakeEvent([["expiration", "not-a-timestamp"]]);

        ValidationResult result = _validator.Validate(evt);

        Assert.False(result.IsValid);
        Assert.Contains("invalid:", result.Reason);
    }
}
