namespace NostrRelay.Core.Tests;

public class NostrEventKindClassifierTests
{
    [Theory]
    [InlineData(0)] // user metadata
    [InlineData(3)] // contacts
    [InlineData(10000)]
    [InlineData(19999)]
    public void Classify_ReturnsReplaceable(int kind) =>
        Assert.Equal(NostrEventKindCategory.Replaceable, NostrEventKindClassifier.Classify(kind));

    [Theory]
    [InlineData(20000)]
    [InlineData(29999)]
    public void Classify_ReturnsEphemeral(int kind) =>
        Assert.Equal(NostrEventKindCategory.Ephemeral, NostrEventKindClassifier.Classify(kind));

    [Theory]
    [InlineData(30000)]
    [InlineData(39999)]
    public void Classify_ReturnsAddressable(int kind) =>
        Assert.Equal(NostrEventKindCategory.Addressable, NostrEventKindClassifier.Classify(kind));

    [Theory]
    [InlineData(1)]     // text note
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(44)]
    [InlineData(1000)]
    [InlineData(9999)]
    public void Classify_ReturnsRegular_ForExplicitlyRegularRanges(int kind) =>
        Assert.Equal(NostrEventKindCategory.Regular, NostrEventKindClassifier.Classify(kind));

    [Theory]
    [InlineData(45)]    // gap between the "4<=n<45" regular range and 1000
    [InlineData(999)]
    [InlineData(40000)] // above the addressable range, below any other defined range
    [InlineData(65535)] // top of the valid kind range (NIP-01: kind is 0-65535)
    public void Classify_DefaultsToRegular_ForKindsNip01DoesNotExplicitlyName(int kind) =>
        Assert.Equal(NostrEventKindCategory.Regular, NostrEventKindClassifier.Classify(kind));

    [Fact]
    public void Classify_BoundaryJustBelowReplaceableRange_IsRegular()
    {
        Assert.Equal(NostrEventKindCategory.Regular, NostrEventKindClassifier.Classify(9999));
        Assert.Equal(NostrEventKindCategory.Replaceable, NostrEventKindClassifier.Classify(10000));
    }

    [Fact]
    public void Classify_BoundaryBetweenReplaceableAndEphemeral()
    {
        Assert.Equal(NostrEventKindCategory.Replaceable, NostrEventKindClassifier.Classify(19999));
        Assert.Equal(NostrEventKindCategory.Ephemeral, NostrEventKindClassifier.Classify(20000));
    }

    [Fact]
    public void Classify_BoundaryBetweenEphemeralAndAddressable()
    {
        Assert.Equal(NostrEventKindCategory.Ephemeral, NostrEventKindClassifier.Classify(29999));
        Assert.Equal(NostrEventKindCategory.Addressable, NostrEventKindClassifier.Classify(30000));
    }

    [Fact]
    public void Classify_ExtensionMethod_MatchesStaticMethod()
    {
        var evt = new NostrEvent
        {
            Id = new string('a', 64),
            Pubkey = new string('b', 64),
            CreatedAt = 1700000000,
            Kind = 30023,
            Tags = [],
            Content = "",
            Sig = new string('c', 128),
        };

        Assert.Equal(NostrEventKindCategory.Addressable, evt.Classify());
    }
}
