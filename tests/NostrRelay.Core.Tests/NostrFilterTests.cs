namespace NostrRelay.Core.Tests;

public class NostrFilterTests
{
    private static NostrEvent MakeEvent(
        string id = "eventid1234567890",
        string pubkey = "authorpubkey1234567890",
        long createdAt = 1700000000,
        int kind = 1,
        IReadOnlyList<IReadOnlyList<string>>? tags = null) => new()
    {
        Id = id,
        Pubkey = pubkey,
        CreatedAt = createdAt,
        Kind = kind,
        Tags = tags ?? [],
        Content = "",
        Sig = new string('a', 128),
    };

    [Fact]
    public void Matches_EmptyFilter_MatchesAnyEvent()
    {
        var filter = new NostrFilter();

        Assert.True(filter.Matches(MakeEvent()));
    }

    [Fact]
    public void Matches_Authors_RequiresExactMatch_NotPrefix()
    {
        // Current NIP-01: "The ids, authors, #e and #p filter lists MUST contain exact
        // 64-character lowercase hex values." Prefix matching is not spec-compliant.
        var filter = new NostrFilter { Authors = ["authorpubkey123"] }; // prefix of the event's pubkey
        NostrEvent evt = MakeEvent(pubkey: "authorpubkey1234567890");

        Assert.False(filter.Matches(evt));
    }

    [Fact]
    public void Matches_Authors_MatchesOnExactValue()
    {
        var filter = new NostrFilter { Authors = ["authorpubkey1234567890"] };
        NostrEvent evt = MakeEvent(pubkey: "authorpubkey1234567890");

        Assert.True(filter.Matches(evt));
    }

    [Fact]
    public void Matches_Ids_RequiresExactMatch_NotPrefix()
    {
        var filter = new NostrFilter { Ids = ["eventid123"] };
        NostrEvent evt = MakeEvent(id: "eventid1234567890");

        Assert.False(filter.Matches(evt));
    }

    [Fact]
    public void Matches_Kinds_UsesExactMembership()
    {
        var filter = new NostrFilter { Kinds = [1, 3] };

        Assert.True(filter.Matches(MakeEvent(kind: 1)));
        Assert.False(filter.Matches(MakeEvent(kind: 2)));
    }

    [Fact]
    public void Matches_SinceAndUntil_AreInclusiveBounds()
    {
        var filter = new NostrFilter { Since = 100, Until = 200 };

        Assert.True(filter.Matches(MakeEvent(createdAt: 100)));
        Assert.True(filter.Matches(MakeEvent(createdAt: 200)));
        Assert.False(filter.Matches(MakeEvent(createdAt: 99)));
        Assert.False(filter.Matches(MakeEvent(createdAt: 201)));
    }

    [Fact]
    public void Matches_TagFilter_MatchesOnFirstTagValueOnly()
    {
        // NIP-01: "Only the first value in any given tag is indexed."
        var filter = new NostrFilter
        {
            TagFilters = new Dictionary<char, IReadOnlyList<string>> { ['e'] = ["target-id"] },
        };

        NostrEvent matching = MakeEvent(tags: [["e", "target-id", "wss://relay.example"]]);
        NostrEvent nonMatching = MakeEvent(tags: [["e", "other-id"]]);

        Assert.True(filter.Matches(matching));
        Assert.False(filter.Matches(nonMatching));
    }

    [Fact]
    public void Matches_MultipleTagFilters_AreAndedTogether()
    {
        var filter = new NostrFilter
        {
            TagFilters = new Dictionary<char, IReadOnlyList<string>>
            {
                ['e'] = ["event-1"],
                ['p'] = ["pubkey-1"]
            },
        };

        NostrEvent hasBoth = MakeEvent(tags: [["e", "event-1"], ["p", "pubkey-1"]]);
        NostrEvent hasOnlyOne = MakeEvent(tags: [["e", "event-1"]]);

        Assert.True(filter.Matches(hasBoth));
        Assert.False(filter.Matches(hasOnlyOne));
    }
}
