using NostrRelay.Core.Validation;

namespace NostrRelay.Core.Tests;

public class StructuralValidatorTests
{
    private readonly StructuralValidator _validator = new();

    private static NostrEvent ValidEvent(int kind = 1) => new()
    {
        Id = new string('a', 64),
        Pubkey = new string('b', 64),
        CreatedAt = 1700000000,
        Kind = kind,
        Tags = [],
        Content = "hello",
        Sig = new string('c', 128)
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void Validate_AcceptsKindsWithinNip01Range(int kind)
    {
        ValidationResult result = _validator.Validate(ValidEvent(kind));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void Validate_RejectsKindsOutsideNip01Range(int kind)
    {
        // NIP-01: "kind": <integer between 0 and 65535>
        ValidationResult result = _validator.Validate(ValidEvent(kind));

        Assert.False(result.IsValid);
        Assert.Contains("kind must be between 0 and 65535", result.Reason);
    }

    [Fact]
    public void Validate_RejectsWrongLengthId()
    {
        NostrEvent evt = ValidEvent() with { Id = new string('a', 63) };

        ValidationResult result = _validator.Validate(evt);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsUppercaseHexId()
    {
        // NIP-01 requires lowercase hex specifically, not just any hex.
        NostrEvent evt = ValidEvent() with { Id = new string('A', 64) };

        ValidationResult result = _validator.Validate(evt);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsNonPositiveCreatedAt()
    {
        NostrEvent evt = ValidEvent() with { CreatedAt = 0 };

        ValidationResult result = _validator.Validate(evt);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsWellFormedEvent()
    {
        ValidationResult result = _validator.Validate(ValidEvent());

        Assert.True(result.IsValid);
    }
}
