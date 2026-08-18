using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NostrRelay.Core;
using NostrRelay.Core.Crypto;
using NostrRelay.Core.Validation;
using NostrRelay.Server.Configuration;
using NostrRelay.Server.Expiration;
using NostrRelay.Server.Info;
using NostrRelay.Server.Metrics;
using NostrRelay.Server.Startup;
using NostrRelay.Server.Subscriptions;
using NostrRelay.Server.WebSockets;
using NostrRelay.Storage.Abstractions;
using NostrRelay.Storage.Postgres;
using NostrRelay.Storage.Sqlite;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Section 4.4/5.1: JSON console formatter for structured logging. Registered on the app's
// own logging pipeline (rather than a separate, disconnected logger factory) so every
// consumer that resolves ILoggerFactory from the container, including EF Core's
// diagnostics for either storage provider below, writes to the same sink as everything
// else.
builder.Logging.AddJsonConsole();

var provider = builder.Configuration.GetValue<string>("Storage:Provider") ?? "Sqlite";
var connectionString = builder.Configuration.GetValue<string>("Storage:ConnectionString");

// Storage service registration branches on the configured provider, since only one
// provider's services should actually exist in the container at a time. Both branches
// register IEventStore as an ordinary DI singleton, constructor-injected like everything
// else, rather than being built by hand before the container exists. Schema/database
// initialization can't happen here, though, since nothing has been resolved from the
// container yet, so that step is deferred to DatabaseInitializationHostedService below.
switch (provider)
{
    case "Sqlite":
        var sqliteConnectionString = connectionString ?? "Data Source=relay.db";

        // ForeignKeys=true is baked into the connection string here rather than inside
        // SqliteEventStore, since the store itself no longer builds its own options; this
        // is the direct equivalent of the old per-connection PRAGMA foreign_keys = ON,
        // applied by Microsoft.Data.Sqlite on every connection open.
        var sqliteConnectionStringBuilder = new SqliteConnectionStringBuilder(sqliteConnectionString)
        {
            ForeignKeys = true,
        };

        builder.Services.AddPooledDbContextFactory<SqliteNostrRelayDbContext>((sp, options) =>
        {
            options.UseSqlite(sqliteConnectionStringBuilder.ConnectionString);
            options.UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>());
        });
        builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
        break;

    case "Postgres":
        var postgresConnectionString = connectionString
            ?? throw new InvalidOperationException("Storage:ConnectionString is required when Storage:Provider is \"Postgres\".");

        // AddNpgsqlDataSource registers a shared NpgsqlDataSource as a singleton that the
        // container owns and disposes. AddPooledDbContextFactory registers
        // IDbContextFactory<PostgresNostrRelayDbContext>, the DI-native equivalent of
        // manually constructing a PooledDbContextFactory<T>. PostgresEventStore takes both
        // as constructor parameters, so it needs no special construction step of its own.
        builder.Services.AddNpgsqlDataSource(postgresConnectionString);
        builder.Services.AddPooledDbContextFactory<PostgresNostrRelayDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), npgsqlOptions => npgsqlOptions.EnableRetryOnFailure());
            options.UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>());
        });
        builder.Services.AddSingleton<IEventStore, PostgresEventStore>();
        break;

    default:
        throw new InvalidOperationException($"Unknown Storage:Provider \"{provider}\". Expected \"Sqlite\" or \"Postgres\".");
}

builder.Services.AddSingleton<ISignatureVerifier, Secp256k1SignatureVerifier>();

// Section 5.6: "Limits", "Policy", and "ExpirationSweep" configuration sections, bound to
// real options types so operators can tune them via appsettings/environment variables
// without a rebuild.
builder.Services.Configure<RelayLimitsOptions>(builder.Configuration.GetSection("Limits"));
builder.Services.Configure<RelayPolicyOptions>(builder.Configuration.GetSection("Policy"));
builder.Services.Configure<ExpirationSweepOptions>(builder.Configuration.GetSection("ExpirationSweep"));

// The full production pipeline (Section 2.3's four rules, plus NIP-40's write-time
// expiration check): structural -> id -> signature -> policy -> expiration.
// EventValidationPipeline.Default() deliberately stays at just the first three (see its
// own doc comment); it's what Core.Tests build against, and changing its signature to
// require policy/expiration config would break Core's independence from any
// hosting-framework configuration type. Composing the full list explicitly here, instead,
// is exactly the extension point Default()'s doc comment anticipated back in Milestone 1.
builder.Services.AddSingleton(sp =>
{
    var signatureVerifier = sp.GetRequiredService<ISignatureVerifier>();
    RelayPolicyOptions policy = sp.GetRequiredService<IOptions<RelayPolicyOptions>>().Value;
    RelayLimitsOptions limits = sp.GetRequiredService<IOptions<RelayLimitsOptions>>().Value;

    return new EventValidationPipeline([
        new StructuralValidator(),
        new IdValidator(),
        new SignatureValidator(signatureVerifier),
        new PolicyValidator(
            policy.PubkeyAllowlist,
            policy.PubkeyBlocklist,
            policy.KindBlocklist,
            limits.CreatedAtLowerLimitSeconds,
            limits.CreatedAtUpperLimitSeconds),
        new ExpirationValidator(),
    ]);
});

// Live publish/subscribe fan-out (Section 5.3): one shared bus, one shared subscription
// registry, one shared connection registry, all singletons since they coordinate across
// every concurrent connection. EventFanOutService is the bus's single background reader.
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<SubscriptionRegistry>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddHostedService<EventFanOutService>();

// NIP-40's background sweep (Milestone 9): reclaims storage from expired events.
// Correctness (never serving an expired event) doesn't depend on this running at any
// particular cadence, that's enforced unconditionally at query time in the storage layer.
builder.Services.AddHostedService<ExpirationSweepService>();

builder.Services.AddSingleton<RelayMetrics>();
builder.Services.AddSingleton<NostrConnectionHandler>();

// Schema must exist before any request is handled. DatabaseInitializationHostedService
// implements IHostedLifecycleService, so the host runs its StartingAsync to completion
// before starting Kestrel (or any other hosted service), see that class's doc comment for
// why that guarantee doesn't hold for an ordinary IHostedService. It resolves IEventStore
// generically, so this works identically regardless of which provider branch above ran.
builder.Services.AddHostedService<DatabaseInitializationHostedService>();

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

// Section 4.4: liveness/readiness for container orchestration. Enumerates at most one row
// via the existing IEventStore.QueryAsync rather than calling CountAsync: a COUNT(*) scan
// (even an index-backed one) does unnecessary work for a probe that's only meant to prove
// the database is reachable, and that cost grows with table size. A single-row query with
// Limit = 1 still exercises storage end to end, so an unreachable database still fails this
// check, without paying for a full scan on every poll.
app.MapGet("/health", async (IEventStore store) =>
{
    try
    {
        await foreach (var _ in store.QueryAsync([new NostrFilter { Limit = 1 }], CancellationToken.None))
            break;

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

// Exposes the otherwise-implicit top-level-statements Program class so
// WebApplicationFactory<Program> can find it in integration tests.
public partial class Program;