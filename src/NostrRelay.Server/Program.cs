using System.Net.WebSockets;
using NostrRelay.Core.Crypto;
using NostrRelay.Core.Validation;
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
    _ => throw new InvalidOperationException($"Unknown Storage:Provider \"{provider}\". Expected \"Sqlite\" or \"Postgres\".")
};

builder.Services.AddSingleton(eventStore);
builder.Services.AddSingleton<ISignatureVerifier, Secp256k1SignatureVerifier>();
builder.Services.AddSingleton(sp => EventValidationPipeline.Default(sp.GetRequiredService<ISignatureVerifier>()));

// Live publish/subscribe fan-out (Section 5.3): one shared bus, one shared subscription
// registry, one shared connection registry, all singletons since they coordinate across
// every concurrent connection. EventFanOutService is the bus's single background reader.
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<SubscriptionRegistry>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddHostedService<EventFanOutService>();

builder.Services.AddSingleton<NostrConnectionHandler>();

WebApplication app = builder.Build();

app.UseWebSockets();

// Relays MUST only accept connections to a single endpoint (NIP-01); NIP-11's relay info
// document will later be served from this same "/" path via content negotiation on the
// Accept header (Milestone 7), so this stays a root-path map rather than "/ws" or similar.
app.Map("/", async (HttpContext context, NostrConnectionHandler handler) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("This endpoint only accepts WebSocket connections.");
        return;
    }

    var connectionId = Guid.NewGuid().ToString("N");
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await handler.HandleAsync(socket, connectionId, context.RequestAborted);
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