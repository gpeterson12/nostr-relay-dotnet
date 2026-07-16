using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NostrRelay.Core;
using NostrRelay.Core.Crypto;
using NostrRelay.Core.Validation;
using NostrRelay.Server.Configuration;
using NostrRelay.Server.Info;
using NostrRelay.Server.Metrics;
using NostrRelay.Server.Subscriptions;
using NostrRelay.Server.WebSockets;
using NostrRelay.Storage.Abstractions;
using NostrRelay.Storage.Postgres;
using NostrRelay.Storage.Sqlite;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

var provider = builder.Configuration.GetValue<string>("Storage:Provider") ?? "Sqlite";
var connectionString = builder.Configuration.GetValue<string>("Storage:ConnectionString");

// Schema must exist before any request is handled, so this runs eagerly here rather than
// as a hosted service startup task. Whichever concrete store gets built, it's registered
// against IEventStore so the rest of the app only ever depends on the storage abstraction.
IEventStore eventStore = provider switch
{
    "Sqlite" => await CreateSqliteStoreAsync(connectionString ?? "Data Source=relay.db"),
    "Postgres" => await CreatePostgresStoreAsync(connectionString
        ?? throw new InvalidOperationException("Storage:ConnectionString is required when Storage:Provider is \"Postgres\".")),
    _ => throw new InvalidOperationException($"Unknown Storage:Provider \"{provider}\". Expected \"Sqlite\" or \"Postgres\"."),
};

builder.Services.AddSingleton(eventStore);
builder.Services.AddSingleton<ISignatureVerifier, Secp256k1SignatureVerifier>();

// Section 5.6: "Limits" and "Policy" configuration sections, bound to real options types
// so operators can tune them via appsettings/environment variables without a rebuild.
builder.Services.Configure<RelayLimitsOptions>(builder.Configuration.GetSection("Limits"));
builder.Services.Configure<RelayPolicyOptions>(builder.Configuration.GetSection("Policy"));

// The full production pipeline (Section 2.3, all four implemented rules in order):
// structural -> id -> signature -> policy. EventValidationPipeline.Default() deliberately
// stays at just the first three (see its own doc comment); it's what Core.Tests build
// against, and changing its signature to require policy config would break Core's
// independence from any hosting-framework configuration type. Composing the full list
// explicitly here, instead, is exactly the extension point Default()'s doc comment
// anticipated back in Milestone 1.
builder.Services.AddSingleton(sp =>
{
    var signatureVerifier = sp.GetRequiredService<ISignatureVerifier>();
    RelayPolicyOptions policy = sp.GetRequiredService<IOptions<RelayPolicyOptions>>().Value;

    return new EventValidationPipeline([
        new StructuralValidator(),
        new IdValidator(),
        new SignatureValidator(signatureVerifier),
        new PolicyValidator(policy.PubkeyAllowlist, policy.PubkeyBlocklist, policy.KindBlocklist),
    ]);
});

// Live publish/subscribe fan-out (Section 5.3): one shared bus, one shared subscription
// registry, one shared connection registry, all singletons since they coordinate across
// every concurrent connection. EventFanOutService is the bus's single background reader.
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<SubscriptionRegistry>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddHostedService<EventFanOutService>();

builder.Services.AddSingleton<RelayMetrics>();
builder.Services.AddSingleton<NostrConnectionHandler>();

WebApplication app = builder.Build();

app.UseWebSockets();

RelayLimitsOptions limitsOptions = app.Services.GetRequiredService<IOptions<RelayLimitsOptions>>().Value;
RelayPolicyOptions policyOptions = app.Services.GetRequiredService<IOptions<RelayPolicyOptions>>().Value;

RelayInfoDocument relayInfoDocument = RelayInfoDocumentFactory.Create(app.Configuration, limitsOptions, policyOptions);
var relayInfoJsonOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// Section 6: three surfaces share this one root path via content negotiation, per NIP-01
// ("relays MUST only accept connections to a single endpoint") and NIP-11 (served "on the
// same URI as the relay's websocket"). /health and /metrics are separate, ordinary routes,
// nothing in either NIP asks for those to share the root path.
app.Map("/", async (HttpContext context, NostrConnectionHandler handler, ConnectionRegistry connections) =>
{
    var acceptHeader = context.Request.Headers.Accept.ToString();

    if (acceptHeader.Contains("application/nostr+json", StringComparison.OrdinalIgnoreCase))
    {
        // NIP-11: "Relays MUST accept CORS requests by sending Access-Control-Allow-Origin,
        // Access-Control-Allow-Headers, and Access-Control-Allow-Methods headers."
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowHeaders = "*";
        context.Response.Headers.AccessControlAllowMethods = "*";
        context.Response.ContentType = "application/nostr+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(relayInfoDocument, relayInfoJsonOptions));
        return;
    }

    if (context.WebSockets.IsWebSocketRequest)
    {
        // Section 5.4: "max concurrent connections (reject new connections past this with
        // a clean WebSocket close + reason)". Checked before AcceptWebSocketAsync rather
        // than accept-then-immediately-close: a plain HTTP rejection here is a cleaner
        // signal than completing a WebSocket handshake just to tear it down a moment
        // later, and it's simpler to reason about.
        if (connections.Count >= limitsOptions.MaxConnections)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("relay is at its configured connection limit");
            return;
        }

        var connectionId = Guid.NewGuid().ToString("N");
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        await handler.HandleAsync(socket, connectionId, context.RequestAborted);
        return;
    }

    context.Response.ContentType = "text/plain";
    await context.Response.WriteAsync(
        "This is a Nostr relay. Connect via WebSocket, or request with header " +
        "'Accept: application/nostr+json' for relay information (NIP-11).");
});

// Section 4.4: liveness/readiness for container orchestration. Actually exercises storage
// (an empty-filter CountAsync) rather than just confirming the process is alive, so it
// catches "the app is up but the database is unreachable" too, not only process crashes.
app.MapGet("/health", async (IEventStore store) =>
{
    try
    {
        await store.CountAsync(new NostrFilter(), CancellationToken.None);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "storage unreachable");
    }
});

// Section 4.4: Prometheus-compatible metrics. Covers connection/subscription/event counts
// for now; query latency histograms and storage size are deliberately not here yet, see
// PrometheusTextFormatter's doc comment for why.
app.MapGet("/metrics", (RelayMetrics metrics, ConnectionRegistry connections, SubscriptionRegistry subscriptions) =>
{
    var text = PrometheusTextFormatter.Format(metrics, connections, subscriptions);
    return Results.Text(text, "text/plain; version=0.0.4");
});

app.Run();
return;

static async Task<IEventStore> CreateSqliteStoreAsync(string connectionString)
{
    var store = new SqliteEventStore(connectionString);
    await store.InitializeAsync();
    return store;
}

static async Task<IEventStore> CreatePostgresStoreAsync(string connectionString)
{
    var store = new PostgresEventStore(connectionString);
    await store.InitializeAsync();
    return store;
}

// Exposes the otherwise-implicit top-level-statements Program class so
// WebApplicationFactory<Program> can find it in integration tests.
public partial class Program;