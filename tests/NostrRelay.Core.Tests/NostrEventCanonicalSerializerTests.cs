using NostrRelay.Core.Serialization;

namespace NostrRelay.Core.Tests;

public class NostrEventCanonicalSerializerTests
{
    // Fixture values are arbitrary; what matters is that both the expected canonical
    // string and its SHA256 were computed independently of this codebase (via a
    // standalone Python script), so this test can't pass just because the
    // implementation and the assertion share the same bug.
    private const string Pubkey = "b0635d6a9851d3aed0cd6c495b282167acf761729078d975fc341b22650b07b";
    private const long CreatedAt = 1700000000;
    private const int Kind = 1;
    private const string Content = "Hello, \"Nostr\"!\nSecond line.";

    private static readonly IReadOnlyList<IReadOnlyList<string>> Tags =
    [
        ["e", "abcd"],
        ["p", "1234"]
    ];

    private const string ExpectedCanonical =
        "[0,\"b0635d6a9851d3aed0cd6c495b282167acf761729078d975fc341b22650b07b\",1700000000,1,[[\"e\",\"abcd\"],[\"p\",\"1234\"]],\"Hello, \\\"Nostr\\\"!\\nSecond line.\"]";

    private const string ExpectedId =
        "eabe50a5e2e85f8f79172e37848e73cdb6513ba3eec7d44d0a5a27dbedcf447b";

    [Fact]
    public void Serialize_ProducesExactCanonicalForm()
    {
        var result = NostrEventCanonicalSerializer.Serialize(Pubkey, CreatedAt, Kind, Tags, Content);

        Assert.Equal(ExpectedCanonical, result);
    }

    [Fact]
    public void ComputeId_MatchesIndependentlyComputedSha256()
    {
        var id = NostrEventCanonicalSerializer.ComputeId(Pubkey, CreatedAt, Kind, Tags, Content);

        Assert.Equal(ExpectedId, id);
    }

    [Fact]
    public void ComputeId_ChangesWhenAnyFieldChanges()
    {
        var baseline = NostrEventCanonicalSerializer.ComputeId(Pubkey, CreatedAt, Kind, Tags, Content);
        var differentContent = NostrEventCanonicalSerializer.ComputeId(Pubkey, CreatedAt, Kind, Tags, "different");
        var differentCreatedAt = NostrEventCanonicalSerializer.ComputeId(Pubkey, CreatedAt + 1, Kind, Tags, Content);

        Assert.NotEqual(baseline, differentContent);
        Assert.NotEqual(baseline, differentCreatedAt);
    }

    [Fact]
    public void Serialize_EscapesControlCharactersButNothingElse()
    {
        var result = NostrEventCanonicalSerializer.Serialize(
            Pubkey, CreatedAt, Kind, tags: [], content: "tab:\t backslash:\\ quote:\" unicode:é");

        // Only the NIP-01 mandated escapes happen; non-ASCII passes through as literal UTF-8,
        // it is not \u-escaped the way System.Text.Json's default encoder would.
        Assert.Contains("tab:\\t backslash:\\\\ quote:\\\" unicode:é", result);
    }
}
