using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using NostrRelay.Core;
using NostrRelay.Core.Protocol;
using NostrRelay.Core.Validation;
using NostrRelay.Server.Subscriptions;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Server.WebSockets;

/// <summary>
/// Handles a single accepted WebSocket connection (Section 5.3). As of Milestone 4, this
/// is a real two-task-per-connection design:
/// <list type="bullet">
/// <item>the read loop (this method's own async context): receives frames, parses them,
/// and either responds directly (OK/EOSE/NOTICE/CLOSED) or publishes to <see cref="EventBus"/>
/// for other connections' subscriptions to pick up</item>
/// <item>a dedicated writer task (<see cref="RunWriterAsync"/>): drains this connection's
/// own bounded outbound channel and is the only code path that ever calls
/// <see cref="WebSocket.SendAsync"/> on this socket</item>
/// </list>
/// Every send, whether it's a direct reply to the client's own message or a live event
/// delivered from another connection's publish via <see cref="EventFanOutService"/>, goes
/// through the same outbound channel. That's what makes concurrent sends from two
/// different sources (this connection's own read loop and the shared fan-out background
/// service) safe: <see cref="WebSocket"/> itself does not support concurrent SendAsync
/// calls, but a single-reader channel does support concurrent writers.
/// </summary>
public sealed class NostrConnectionHandler(
    IEventStore eventStore,
    EventValidationPipeline validationPipeline,
    EventBus bus,
    SubscriptionRegistry subscriptions,
    ConnectionRegistry connections,
    ILogger<NostrConnectionHandler> logger)
{
    private const int ReceiveChunkBytes = 8192;

    /// <summary>Raw message size cap (Section 4.3), matching the spec's
    /// <c>Limits:MaxEventSizeBytes</c> default.</summary>
    private const int MaxMessageBytes = 65536;

    /// <summary>Section 5.4: bounded outbound channel per connection, with an explicit
    /// drop policy once full. Drop-oldest rather than disconnect: a momentarily slow
    /// client loses its oldest still-unsent live events rather than being kicked, which
    /// is the friendlier default; becomes configurable in Milestone 8.</summary>
    private const int OutboundChannelCapacity = 256;

    public async Task HandleAsync(WebSocket socket, string connectionId, CancellationToken ct)
    {
        using IDisposable? _ = logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connectionId });
        logger.LogInformation("connection opened");

        var outbound = Channel.CreateBounded<RelayMessage>(new BoundedChannelOptions(OutboundChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        connections.Register(connectionId, outbound.Writer);
        Task writerTask = RunWriterAsync(socket, outbound.Reader, ct);

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? message;
                try
                {
                    message = await ReceiveTextMessageAsync(socket, ct);
                }
                catch (InvalidOperationException ex)
                {
                    await outbound.Writer.WriteAsync(new NoticeRelayMessage($"invalid: {ex.Message}"), ct);
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or IOException)
                {
                    // Abnormal client disconnect. Surfaces as WebSocketException in
                    // production but can surface as IOException (wrapping
                    // ObjectDisposedException) when a test disposes its socket without
                    // sending a graceful Close frame, hence catching both.
                    break;
                }

                if (message is null)
                    break; // client sent a Close frame

                await ProcessMessageAsync(connectionId, outbound.Writer, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or connection aborted; nothing more to do.
        }
        finally
        {
            // Order matters: stop new subscription matches and new lookups from finding
            // this connection before tearing down its channel, so EventFanOutService
            // can't be mid-TryGetWriter against a writer we're about to complete.
            subscriptions.RemoveConnection(connectionId);
            connections.Unregister(connectionId);

            outbound.Writer.TryComplete();
            try
            {
                await writerTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException)
                {
                    // Best-effort close; the client may have already disconnected. Surfaces
                    // as WebSocketException in production but can surface as IOException
                    // (wrapping ObjectDisposedException) against TestWebSocket and in some
                    // real ClientWebSocket disconnect races, hence catching both.
                }
            }

            logger.LogInformation("connection closed");
        }
    }

    /// <summary>The sole writer of this connection's socket. Drains the outbound channel
    /// until it's completed (connection closing) or the socket errors out.</summary>
    private async Task RunWriterAsync(WebSocket socket, ChannelReader<RelayMessage> reader, CancellationToken ct)
    {
        try
        {
            await foreach (RelayMessage message in reader.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open)
                    break;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(message.ToJson());
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException)
                {
                    logger.LogDebug(ex, "failed to send message, client likely disconnected");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessMessageAsync(string connectionId, ChannelWriter<RelayMessage> outbound, string rawMessage, CancellationToken ct)
    {
        ClientMessage clientMessage;
        try
        {
            clientMessage = ClientMessageParser.Parse(rawMessage);
        }
        catch (NostrProtocolException ex)
        {
            await outbound.WriteAsync(new NoticeRelayMessage($"error: {ex.Message}"), ct);
            return;
        }

        switch (clientMessage)
        {
            case EventClientMessage eventMessage:
                await HandleEventAsync(outbound, eventMessage.Event, ct);
                break;

            case ReqClientMessage reqMessage:
                await HandleReqAsync(connectionId, outbound, reqMessage, ct);
                break;

            case CloseClientMessage closeMessage:
                subscriptions.Remove(connectionId, closeMessage.SubscriptionId);
                break;

            case AuthClientMessage or CountClientMessage:
                await outbound.WriteAsync(new NoticeRelayMessage("error: AUTH and COUNT are not yet supported"), ct);
                break;

            default:
                throw new InvalidOperationException($"unhandled client message type: {clientMessage.GetType().Name}");
        }
    }

    private async Task HandleEventAsync(ChannelWriter<RelayMessage> outbound, NostrEvent evt, CancellationToken ct)
    {
        ValidationResult validation = validationPipeline.Validate(evt);
        if (!validation.IsValid)
        {
            await outbound.WriteAsync(new OkRelayMessage(evt.Id, false, validation.Reason ?? "invalid: event failed validation"), ct);
            return;
        }

        PersistResult result;
        try
        {
            result = await eventStore.SaveEventAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "failed to persist event {EventId}", evt.Id);
            await outbound.WriteAsync(new OkRelayMessage(evt.Id, false, "error: could not persist event"), ct);
            return;
        }

        // Broadcast genuinely new content: a freshly stored event (regular, or the new
        // latest for a replaceable/addressable key) and ephemeral events, whose only
        // delivery path is this live broadcast, they're never queryable historically.
        // Duplicate and Superseded are deliberately not broadcast: nothing new happened.
        if (result.Outcome is PersistOutcome.Stored or PersistOutcome.Ephemeral)
            await bus.PublishAsync(evt, ct);

        var (accepted, message) = MapPersistResult(result);
        await outbound.WriteAsync(new OkRelayMessage(evt.Id, accepted, message), ct);
    }

    private static (bool Accepted, string Message) MapPersistResult(PersistResult result) => result.Outcome switch
    {
        PersistOutcome.Stored => (true, ""),
        PersistOutcome.Duplicate => (true, "duplicate: already have this event"),
        PersistOutcome.Superseded => (true, ""),
        PersistOutcome.Ephemeral => (true, ""),
        _ => (false, "error: unrecognized persistence outcome"),
    };

    private async Task HandleReqAsync(string connectionId, ChannelWriter<RelayMessage> outbound, ReqClientMessage reqMessage, CancellationToken ct)
    {
        if (!subscriptions.TryAddOrReplace(connectionId, reqMessage.SubscriptionId, reqMessage.Filters))
        {
            await outbound.WriteAsync(new ClosedRelayMessage(reqMessage.SubscriptionId, "error: too many subscriptions on this connection"), ct);
            return;
        }

        // Registered before the historical query runs, not after: this means an event
        // published concurrently with this query might arrive via live fan-out and be
        // sent before (or interleaved with) the historical replay below, occasionally
        // producing a harmless duplicate delivery of the same event. The alternative,
        // registering after, risks the opposite and worse failure: missing an event
        // published in the gap between the query finishing and the subscription
        // existing. Clients are expected to dedupe by id regardless (standard Nostr
        // client behavior), so the duplicate-favoring direction is the safe one.
        await foreach (NostrEvent evt in eventStore.QueryAsync(reqMessage.Filters, ct))
            await outbound.WriteAsync(new EventRelayMessage(reqMessage.SubscriptionId, evt), ct);

        await outbound.WriteAsync(new EoseRelayMessage(reqMessage.SubscriptionId), ct);
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[ReceiveChunkBytes];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);

            if (stream.Length > MaxMessageBytes)
                throw new InvalidOperationException($"message exceeds maximum size of {MaxMessageBytes} bytes");
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}