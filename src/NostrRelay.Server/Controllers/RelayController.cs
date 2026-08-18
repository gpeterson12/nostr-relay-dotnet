using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NostrRelay.Server.Configuration;
using NostrRelay.Server.Info;
using NostrRelay.Server.Subscriptions;
using NostrRelay.Server.WebSockets;

namespace NostrRelay.Server.Controllers;

// Section 6: three surfaces share this one root path via content negotiation, per NIP-01
// ("relays MUST only accept connections to a single endpoint") and NIP-11 (served "on the
// same URI as the relay's websocket"). All three surfaces are handled here in one action
// rather than split across [HttpGet] overloads, since the branching *is* the content
// negotiation, not three independent routes.
//
// RelayInfoDocument, its JsonSerializerOptions, and IOptions<RelayLimitsOptions> are
// constructor-injected rather than resolved ad hoc, so the controller's dependencies are
// visible in one place and IOptions is re-read per request rather than frozen at startup.
[ApiController]
[Route("/")]
public sealed class RelayController(
    NostrConnectionHandler handler,
    ConnectionRegistry connections,
    RelayInfoDocument relayInfoDocument,
    JsonSerializerOptions relayInfoJsonOptions,
    IOptions<RelayLimitsOptions> limits)
    : ControllerBase
{
    private readonly RelayLimitsOptions _limits = limits.Value;

    [HttpGet]
    public async Task Get()
    {
        var acceptHeader = Request.Headers.Accept.ToString();

        if (acceptHeader.Contains("application/nostr+json", StringComparison.OrdinalIgnoreCase))
        {
            // NIP-11: "Relays MUST accept CORS requests by sending Access-Control-Allow-Origin,
            // Access-Control-Allow-Headers, and Access-Control-Allow-Methods headers."
            Response.Headers.AccessControlAllowOrigin = "*";
            Response.Headers.AccessControlAllowHeaders = "*";
            Response.Headers.AccessControlAllowMethods = "*";
            Response.ContentType = "application/nostr+json";
            await Response.WriteAsync(JsonSerializer.Serialize(relayInfoDocument, relayInfoJsonOptions));
            return;
        }

        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            // Section 5.4: "max concurrent connections (reject new connections past this with
            // a clean WebSocket close + reason)". Checked before AcceptWebSocketAsync rather
            // than accept-then-immediately-close: a plain HTTP rejection here is a cleaner
            // signal than completing a WebSocket handshake just to tear it down a moment
            // later, and it's simpler to reason about.
            if (connections.Count >= _limits.MaxConnections)
            {
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await Response.WriteAsync("relay is at its configured connection limit");
                return;
            }

            var connectionId = Guid.NewGuid().ToString("N");
            using WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await handler.HandleAsync(socket, connectionId, HttpContext.RequestAborted);
            return;
        }

        Response.ContentType = "text/plain";
        await Response.WriteAsync(
            "This is a Nostr relay. Connect via WebSocket, or request with header " +
            "'Accept: application/nostr+json' for relay information (NIP-11).");
    }
}