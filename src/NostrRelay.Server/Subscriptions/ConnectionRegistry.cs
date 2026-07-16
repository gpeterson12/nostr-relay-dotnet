using System.Collections.Concurrent;
using System.Threading.Channels;
using NostrRelay.Core.Protocol;

namespace NostrRelay.Server.Subscriptions;

/// <summary>
/// Maps a live connection id to the <see cref="ChannelWriter{T}"/> that feeds its outbound
/// WebSocket writer task (Section 5.3). Kept separate from <see cref="SubscriptionRegistry"/>
/// so that filter-matching code never touches transport concerns, and so a connection can
/// be registered here the moment it's accepted, before any subscription exists.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ChannelWriter<RelayMessage>> _outboxes = new();

    public void Register(string connectionId, ChannelWriter<RelayMessage> writer) =>
        _outboxes[connectionId] = writer;

    public void Unregister(string connectionId) =>
        _outboxes.TryRemove(connectionId, out _);

    public bool TryGetWriter(string connectionId, out ChannelWriter<RelayMessage> writer) =>
        _outboxes.TryGetValue(connectionId, out writer!);
}
