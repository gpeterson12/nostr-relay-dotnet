using System.Net.WebSockets;
using System.Text;
using NostrRelay.Core;
using NostrRelay.Core.Protocol;
using NostrRelay.Core.Validation;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Server.WebSockets;

/// <summary>
/// Handles a single accepted WebSocket connection end to end: reads frames, parses them
/// into <see cref="ClientMessage"/>s, and responds. This is the Milestone 3 "minimal"
/// version (Section 8): <c>REQ</c> runs one historical query and sends <c>EOSE</c>, it
/// does not register a live, ongoing subscription. That's the Channels-based bus and
/// <c>SubscriptionRegistry</c> from Section 5.3, added in Milestone 4.
///
/// Because there's no live fan-out yet, all sends on the socket happen sequentially from
/// this one read-process-respond loop, there's no concurrent writer that could race with
/// it. Once Milestone 4 adds a background bus consumer that can push EVENT messages onto
/// this same connection asynchronously, sends will need to be serialized (a bounded
/// outbound channel drained by a single writer task, per Section 5.3) since
/// <see cref="WebSocket"/> does not support concurrent SendAsync calls.
/// </summary>
public sealed class NostrConnectionHandler(
    IEventStore eventStore,
    EventValidationPipeline validationPipeline,
    ILogger<NostrConnectionHandler> logger)
{
    private const int ReceiveChunkBytes = 8192;

    /// <summary>Raw message size cap (Section 4.3). Matches the spec's
    /// <c>Limits:MaxEventSizeBytes</c> default; becomes independently configurable once
    /// the policy layer lands (Milestone 8).</summary>
    private const int MaxMessageBytes = 65536;

    public async Task HandleAsync(WebSocket socket, string connectionId, CancellationToken ct)
    {
        using IDisposable? _ = logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connectionId });
        logger.LogInformation("connection opened");

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
                    // Oversized message: notify, then close rather than continue reading
                    // an already-desynchronized stream.
                    await TrySendAsync(socket, new NoticeRelayMessage($"invalid: {ex.Message}"), ct);
                    break;
                }
                catch (WebSocketException)
                {
                    break; // abnormal client disconnect
                }

                if (message is null)
                    break; // client sent a Close frame

                await ProcessMessageAsync(socket, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or connection aborted; nothing more to do.
        }
        finally
        {
            // Open: we broke out of the loop ourselves (cancellation, oversized message,
            // etc.) and need to initiate the close handshake.
            // CloseReceived: the client already sent its Close frame (ReceiveTextMessageAsync
            // returned null for it) and is waiting for ours back to complete the handshake
            // cleanly.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // Best-effort close; the client may have already disconnected.
                }
            }

            logger.LogInformation("connection closed");
        }
    }

    private async Task ProcessMessageAsync(WebSocket socket, string rawMessage, CancellationToken ct)
    {
        ClientMessage clientMessage;
        try
        {
            clientMessage = ClientMessageParser.Parse(rawMessage);
        }
        catch (NostrProtocolException ex)
        {
            await TrySendAsync(socket, new NoticeRelayMessage($"error: {ex.Message}"), ct);
            return;
        }

        switch (clientMessage)
        {
            case EventClientMessage eventMessage:
                await HandleEventAsync(socket, eventMessage.Event, ct);
                break;

            case ReqClientMessage reqMessage:
                await HandleReqAsync(socket, reqMessage, ct);
                break;

            case CloseClientMessage closeMessage:
                // No live subscriptions exist yet, so there's nothing to tear down.
                // Logged rather than ignored so the no-op is visible while diagnosing
                // client behavior during this milestone.
                logger.LogDebug(
                    "CLOSE received for subscription {SubscriptionId}; no-op until live subscriptions exist (Milestone 4)",
                    closeMessage.SubscriptionId);
                break;

            case AuthClientMessage or CountClientMessage:
                await TrySendAsync(socket, new NoticeRelayMessage("error: AUTH and COUNT are not yet supported"), ct);
                break;

            default:
                throw new InvalidOperationException($"unhandled client message type: {clientMessage.GetType().Name}");
        }
    }

    private async Task HandleEventAsync(WebSocket socket, NostrEvent evt, CancellationToken ct)
    {
        ValidationResult validation = validationPipeline.Validate(evt);
        if (!validation.IsValid)
        {
            await TrySendAsync(socket, new OkRelayMessage(evt.Id, false, validation.Reason ?? "invalid: event failed validation"), ct);
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
            await TrySendAsync(socket, new OkRelayMessage(evt.Id, false, "error: could not persist event"), ct);
            return;
        }

        var (accepted, message) = MapPersistResult(result);
        await TrySendAsync(socket, new OkRelayMessage(evt.Id, accepted, message), ct);
    }

    /// <summary>
    /// Maps storage outcomes to OK's accepted flag and message, per the recommendation
    /// documented on <see cref="PersistResult"/>: a duplicate is still accepted (NIP-01
    /// shows this explicitly), and a superseded replaceable/addressable event is accepted
    /// too, it was valid, it just isn't the copy the relay keeps.
    /// </summary>
    private static (bool Accepted, string Message) MapPersistResult(PersistResult result) => result.Outcome switch
    {
        PersistOutcome.Stored => (true, ""),
        PersistOutcome.Duplicate => (true, "duplicate: already have this event"),
        PersistOutcome.Superseded => (true, ""),
        PersistOutcome.Ephemeral => (true, ""),
        _ => (false, "error: unrecognized persistence outcome")
    };

    private async Task HandleReqAsync(WebSocket socket, ReqClientMessage reqMessage, CancellationToken ct)
    {
        // Milestone 3 scope: one historical query, then EOSE. No subscription is
        // registered, so no further EVENT messages will arrive for this subscription id
        // once EOSE is sent (that's the live fan-out bus, Milestone 4).
        await foreach (NostrEvent evt in eventStore.QueryAsync(reqMessage.Filters, ct))
            await TrySendAsync(socket, new EventRelayMessage(reqMessage.SubscriptionId, evt), ct);

        await TrySendAsync(socket, new EoseRelayMessage(reqMessage.SubscriptionId), ct);
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

            if (stream.Length > MaxMessageBytes)
                throw new InvalidOperationException($"message exceeds maximum size of {MaxMessageBytes} bytes");
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task TrySendAsync(WebSocket socket, RelayMessage message, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJson());
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "failed to send message, client likely disconnected");
        }
    }
}