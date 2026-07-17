namespace NostrRelay.Core.Tests;

public class DeletionRequestTests
{
    private static NostrEvent MakeDeletionEvent(string pubkey, IReadOnlyList<IReadOnlyList<string>> tags) => new()
    {
        Id = new string('a', 64),
        Pubkey = pubkey,
        CreatedAt = 1700000000,
        Kind = 5,
        Tags = tags,
        Content = "",
        Sig = new string('b', 128),
    };

    [Fact]
    public void Parse_ExtractsEventIdsFromETags()
    {
        NostrEvent evt = MakeDeletionEvent("author-pubkey", [["e", "event-id-1"], ["e", "event-id-2"]]);

        DeletionRequest request = DeletionRequest.Parse(evt);

        Assert.Equal(["event-id-1", "event-id-2"], request.EventIds);
    }

    [Fact]
    public void Parse_ExtractsAddressableCoordinateFromMatchingATag()
    {
        NostrEvent evt = MakeDeletionEvent("author-pubkey", [["a", "30023:author-pubkey:my-article"]]);

        DeletionRequest request = DeletionRequest.Parse(evt);

        AddressableCoordinate coordinate = Assert.Single(request.AddressableCoordinates);
        Assert.Equal(30023, coordinate.Kind);
        Assert.Equal("author-pubkey", coordinate.Pubkey);
        Assert.Equal("my-article", coordinate.DTag);
    }

    [Fact]
    public void Parse_DropsATagWhenCoordinatePubkeyDoesNotMatchDeletionRequestAuthor()
    {
        // Guards against naming someone else's addressable event in a forged-looking "a"
        // tag: the coordinate's embedded pubkey must match who's actually asking.
        NostrEvent evt = MakeDeletionEvent("author-pubkey", [["a", "30023:someone-elses-pubkey:their-article"]]);

        DeletionRequest request = DeletionRequest.Parse(evt);

        Assert.Empty(request.AddressableCoordinates);
    }

    [Fact]
    public void Parse_DTagWithColons_ParsesEntireRemainderAsIdentifier()
    {
        NostrEvent evt = MakeDeletionEvent("author-pubkey", [["a", "30023:author-pubkey:section:subsection:1"]]);

        DeletionRequest request = DeletionRequest.Parse(evt);

        AddressableCoordinate coordinate = Assert.Single(request.AddressableCoordinates);
        Assert.Equal("section:subsection:1", coordinate.DTag);
    }

    [Fact]
    public void Parse_MalformedATag_IsIgnored()
    {
        NostrEvent evt = MakeDeletionEvent("author-pubkey", [["a", "not-a-valid-coordinate"]]);

        DeletionRequest request = DeletionRequest.Parse(evt);

        Assert.Empty(request.AddressableCoordinates);
    }

    [Fact]
    public void Parse_IgnoresUnrelatedTags()
    {
        NostrEvent evt = MakeDeletionEvent("author-pubkey", [["k", "1"], ["e", "event-id-1"]]);

        DeletionRequest request = DeletionRequest.Parse(evt);

        Assert.Equal(["event-id-1"], request.EventIds);
    }

    [Fact]
    public void Parse_NoRelevantTags_ReturnsEmptyRequest()
    {
        NostrEvent evt = MakeDeletionEvent("author-pubkey", []);

        DeletionRequest request = DeletionRequest.Parse(evt);

        Assert.Empty(request.EventIds);
        Assert.Empty(request.AddressableCoordinates);
    }
}
