using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NostrRelay.Core;
using NostrRelay.Core.Protocol;
using NostrRelay.Core.Validation;
using NostrRelay.Server.Configuration;
using NostrRelay.Server.Metrics;
using NostrRelay.Server.RateLimiting;
using NostrRelay.Server.Subscriptions;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Server.WebSockets;

/// <summary>
/// Handles a single accepted WebSocket connection (Section 5.3). Two-task-per-connection
/// design established in Milestone 4:
/// <list type="bullet">
/// <item>the read loop (this method's own async context): receives frames, parses them,
/// and either responds directly (OK/EOSE/NOTICE/CLOSED) or publishes to <see cref="EventBus"/>
/// for other connections' subscriptions to pick up</item>
/// <item>a dedicated writer task (<see cref="RunWriterAsync"/>): drains this connection's
/// own bounded outbound channel and is the only code path that ever calls
/// <see cref="WebSocket.SendAsync"/> on this socket</item>
/// </list>
///
/// As of Milestone 8, each connection also gets two <see cref="TokenBucketRateLimiter"/>
/// instances (EVENT publishes and REQ subscriptions, Section 4.3: "Rate limit per
/// connection... for both EVENT publishes and REQ subscriptions"), created fresh per
/// connection alongside the outbound channel, sharing the same per-connection lifecycle.
/// </summary>
public sealed class NostrConnectionHandler(
    IEventStore eventStore,
    EventValidationPipeline validationPipeline,
    EventBus bus,
    SubscriptionRegistry subscriptions,
    ConnectionRegistry connections,
    RelayMetrics metrics,
    IOptions<RelayLimitsOptions> limitsOptions,
    ILogger<NostrConnectionHandler> logger)
{
    private const int ReceiveChunkBytes = 8192;

    /// <summary>Section 5.4: bounded outbound channel per connection, with an explicit
    /// drop policy once full. Drop-oldest rather than disconnect: a momentarily slow
    /// client loses its oldest still-unsent live events rather than being kicked, which
    /// is the friendlier default; becomes configurable alongside the rest of
    /// RelayLimitsOptions if a real need for tuning it shows up.</summary>
    private const int OutboundChannelCapacity = 256;

    private readonly RelayLimitsOptions _limits = limitsOptions.Value;

    public async Task HandleAsync(WebSocket socket, string connectionId, CancellationToken ct)
    {
        using IDisposable? _ = logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connectionId });
        logger.LogInformation("connection opened");
        metrics.RecordConnectionOpened();

        var outbound = Channel.CreateBounded<RelayMessage>(new BoundedChannelOptions(OutboundChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // One token bucket per operation type, not one shared bucket: a burst of REQs
        // shouldn't eat into the budget for EVENT publishes or vice versa, they're
        // different abuse patterns with the same configured rate as a starting point.
        TimeSpan refillPeriod = TimeSpan.FromMinutes(1);
        var eventRateLimiter = new TokenBucketRateLimiter(_limits.EventRateLimitPerMinute, refillPeriod);
        var reqRateLimiter = new TokenBucketRateLimiter(_limits.EventRateLimitPerMinute, refillPeriod);

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

                await ProcessMessageAsync(connectionId, outbound.Writer, message, eventRateLimiter, reqRateLimiter, ct);
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

    private async Task ProcessMessageAsync(
        string connectionId,
        ChannelWriter<RelayMessage> outbound,
        string rawMessage,
        TokenBucketRateLimiter eventRateLimiter,
        TokenBucketRateLimiter reqRateLimiter,
        CancellationToken ct)
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
                await HandleEventAsync(outbound, eventMessage.Event, eventRateLimiter, ct);
                break;

            case ReqClientMessage reqMessage:
                await HandleReqAsync(connectionId, outbound, reqMessage, reqRateLimiter, ct);
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

    private async Task HandleEventAsync(
        ChannelWriter<RelayMessage> outbound, NostrEvent evt, TokenBucketRateLimiter rateLimiter, CancellationToken ct)
    {
        // Rate limit checked first, before any validation work: it's the cheapest
        // possible rejection and protects against a flood of events regardless of
        // whether any of them would otherwise be valid (Section 2.3's "cheap checks
        // first" ordering philosophy applies here too, rate limiting is cheaper than
        // structural validation).
        if (!rateLimiter.TryConsume())
        {
            metrics.RecordEventRejected("rate-limited");
            await outbound.WriteAsync(new OkRelayMessage(evt.Id, false, "rate-limited: slow down"), ct);
            return;
        }

        ValidationResult validation = validationPipeline.Validate(evt);
        if (!validation.IsValid)
        {
            var reason = validation.Reason ?? "invalid: event failed validation";
            metrics.RecordEventRejected(ReasonPrefix(reason));
            await outbound.WriteAsync(new OkRelayMessage(evt.Id, false, reason), ct);
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
            metrics.RecordEventRejected("error");
            await outbound.WriteAsync(new OkRelayMessage(evt.Id, false, "error: could not persist event"), ct);
            return;
        }

        metrics.RecordEventIngested();

        // Broadcast genuinely new content: a freshly stored event (regular, or the new
        // latest for a replaceable/addressable key) and ephemeral events, whose only
        // delivery path is this live broadcast, they're never queryable historically.
        // Duplicate and Superseded are deliberately not broadcast: nothing new happened.
        if (result.Outcome is PersistOutcome.Stored or PersistOutcome.Ephemeral)
            await bus.PublishAsync(evt, ct);

        var (accepted, message) = MapPersistResult(result);
        await outbound.WriteAsync(new OkRelayMessage(evt.Id, accepted, message), ct);
    }

    /// <summary>Extracts the standardized OK-message prefix (Section 2.2, e.g. "invalid",
    /// "blocked") from a full reason string like "invalid: kind must be...", for grouping
    /// the <c>nostr_relay_events_rejected_total</c> metric by reason.</summary>
    private static string ReasonPrefix(string reason)
    {
        var colonIndex = reason.IndexOf(':');
        return colonIndex > 0 ? reason[..colonIndex] : reason;
    }

    private static (bool Accepted, string Message) MapPersistResult(PersistResult result) => result.Outcome switch
    {
        PersistOutcome.Stored => (true, ""),
        PersistOutcome.Duplicate => (true, "duplicate: already have this event"),
        PersistOutcome.Superseded => (true, ""),
        PersistOutcome.Ephemeral => (true, ""),
        _ => (false, "error: unrecognized persistence outcome"),
    };

    private async Task HandleReqAsync(
        string connectionId,
        ChannelWriter<RelayMessage> outbound,
        ReqClientMessage reqMessage,
        TokenBucketRateLimiter rateLimiter,
        CancellationToken ct)
    {
        if (!rateLimiter.TryConsume())
        {
            await outbound.WriteAsync(new ClosedRelayMessage(reqMessage.SubscriptionId, "rate-limited: slow down"), ct);
            return;
        }

        if (reqMessage.Filters.Count > _limits.MaxFiltersPerSubscription)
        {
            await outbound.WriteAsync(new ClosedRelayMessage(
                reqMessage.SubscriptionId, $"invalid: too many filters (max {_limits.MaxFiltersPerSubscription})"), ct);
            return;
        }

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

    private async Task<string?> ReceiveTextMessageAsync(WebSocket socket, CancellationToken ct)
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

            if (stream.Length > _limits.MaxEventSizeBytes)
                throw new InvalidOperationException($"message exceeds maximum size of {_limits.MaxEventSizeBytes} bytes");
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}