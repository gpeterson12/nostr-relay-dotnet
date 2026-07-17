using System.Net.WebSockets;
using System.Text.Json;
using NBitcoin.Secp256k1;
using NostrRelay.Core;
using NostrRelay.Server.IntegrationTests.TestSupport;
using static NostrRelay.Server.IntegrationTests.TestSupport.NostrTestEvents;

namespace NostrRelay.Server.IntegrationTests;

public class DeletionTests
{
    [Fact]
    public async Task Deletion_ByETag_RemovesEventAuthoredBySameKey()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("deletion-own-post-seed");
        NostrEvent post = SignWithKey(privkey, pubkeyHex, "oops, posted by accident");

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(post)}}]""");
        await socket.ReceiveUntilAsync("OK");

        NostrEvent deletionRequest = SignWithKey(privkey, pubkeyHex, "accidental post", kind: 5, tags: [["e", post.Id]]);
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(deletionRequest)}}]""");
        JsonElement deletionOk = await socket.ReceiveUntilAsync("OK");
        Assert.True(deletionOk[2].GetBoolean());

        await socket.SendAsync($$"""["REQ", "sub1", {"ids": ["{{post.Id}}"]}]""");
        JsonElement response = await socket.ReceiveUntilAsync("EOSE");
        Assert.Equal("EOSE", response[0].GetString());
        // No EVENT should have arrived before this EOSE; ReceiveUntilAsync would have
        // returned the EVENT first if one existed, so reaching EOSE directly confirms
        // the deleted post is genuinely gone.
    }

    [Fact]
    public async Task Deletion_ByETag_DoesNotRemoveEventAuthoredByDifferentKey()
    {
        (ECPrivKey victimPrivkey, var victimPubkey) = GenerateKeyPair("deletion-victim-seed");
        (ECPrivKey attackerPrivkey, var attackerPubkey) = GenerateKeyPair("deletion-attacker-seed");

        NostrEvent victimPost = SignWithKey(victimPrivkey, victimPubkey, "legitimate post");

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(victimPost)}}]""");
        await socket.ReceiveUntilAsync("OK");

        // Attacker tries to delete the victim's post by referencing its id from their own,
        // differently-authored, deletion request.
        NostrEvent forgedDeletion = SignWithKey(attackerPrivkey, attackerPubkey, "trying to delete someone else's post", kind: 5, tags: [["e", victimPost.Id]]);
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(forgedDeletion)}}]""");
        JsonElement deletionOk = await socket.ReceiveUntilAsync("OK");
        Assert.True(deletionOk[2].GetBoolean()); // the deletion request event itself is still valid and gets stored

        await socket.SendAsync($$"""["REQ", "sub1", {"ids": ["{{victimPost.Id}}"]}]""");
        JsonElement eventMessage = await socket.ReceiveUntilAsync("EVENT");
        Assert.Equal(victimPost.Id, eventMessage[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Deletion_ByATag_RemovesAddressableEvent()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("deletion-addressable-seed");
        NostrEvent article = SignWithKey(privkey, pubkeyHex, "first draft", kind: 30023, tags: [["d", "my-article"]]);

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(article)}}]""");
        await socket.ReceiveUntilAsync("OK");

        var coordinate = $"30023:{pubkeyHex}:my-article";
        NostrEvent deletionRequest = SignWithKey(privkey, pubkeyHex, "retracting this article", kind: 5, tags: [["a", coordinate]]);
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(deletionRequest)}}]""");
        await socket.ReceiveUntilAsync("OK");

        await socket.SendAsync($$"""["REQ", "sub1", {"authors": ["{{pubkeyHex}}"], "kinds": [30023]}]""");
        JsonElement response = await socket.ReceiveUntilAsync("EOSE");
        Assert.Equal("EOSE", response[0].GetString());
    }

    [Fact]
    public async Task Deletion_KindFiveCannotDeleteAnotherKindFive()
    {
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("deletion-of-deletion-seed");
        NostrEvent firstDeletion = SignWithKey(privkey, pubkeyHex, "first deletion request", kind: 5, tags: [["e", new string('9', 64)]]);

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(firstDeletion)}}]""");
        await socket.ReceiveUntilAsync("OK");

        // NIP-09: "Publishing a deletion request event against a deletion request has no effect."
        NostrEvent secondDeletion = SignWithKey(privkey, pubkeyHex, "trying to delete the first deletion request", kind: 5, tags: [["e", firstDeletion.Id]]);
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(secondDeletion)}}]""");
        await socket.ReceiveUntilAsync("OK");

        await socket.SendAsync($$"""["REQ", "sub1", {"ids": ["{{firstDeletion.Id}}"]}]""");
        JsonElement eventMessage = await socket.ReceiveUntilAsync("EVENT");
        Assert.Equal(firstDeletion.Id, eventMessage[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Deletion_RequestEventItselfRemainsQueryable()
    {
        // NIP-09: "Relays SHOULD continue to publish/share the deletion request events
        // indefinitely, as clients may already have the event that's intended to be deleted."
        (ECPrivKey privkey, var pubkeyHex) = GenerateKeyPair("deletion-request-persists-seed");
        NostrEvent post = SignWithKey(privkey, pubkeyHex, "a post");

        await using var factory = new NostrRelayWebApplicationFactory();
        using WebSocket socket = await factory.ConnectAsync();

        await socket.SendAsync($$"""["EVENT", {{ToEventJson(post)}}]""");
        await socket.ReceiveUntilAsync("OK");

        NostrEvent deletionRequest = SignWithKey(privkey, pubkeyHex, "removing my post", kind: 5, tags: [["e", post.Id]]);
        await socket.SendAsync($$"""["EVENT", {{ToEventJson(deletionRequest)}}]""");
        await socket.ReceiveUntilAsync("OK");

        await socket.SendAsync($$"""["REQ", "sub1", {"ids": ["{{deletionRequest.Id}}"]}]""");
        JsonElement eventMessage = await socket.ReceiveUntilAsync("EVENT");
        Assert.Equal(deletionRequest.Id, eventMessage[2].GetProperty("id").GetString());
    }
}
