using System.Net.WebSockets;
using NostrRelay.Core.Crypto;
using NostrRelay.Core.Validation;
using NostrRelay.Server.WebSockets;
using NostrRelay.Storage.Abstractions;
using NostrRelay.Storage.Sqlite;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetValue<string>("Storage:ConnectionString")
    ?? "Data Source=relay.db";

// Schema must exist before any request is handled, so this runs eagerly here rather than
// as a hosted service startup task. SqliteEventStore is registered against IEventStore so
// the rest of the app only ever depends on the storage abstraction, never the concrete type.
var eventStore = new SqliteEventStore(connectionString);
await eventStore.InitializeAsync();

builder.Services.AddSingleton<IEventStore>(eventStore);
builder.Services.AddSingleton<ISignatureVerifier, Secp256k1SignatureVerifier>();
builder.Services.AddSingleton(sp => EventValidationPipeline.Default(sp.GetRequiredService<ISignatureVerifier>()));
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

// Exposes the otherwise-implicit top-level-statements Program class so
// WebApplicationFactory<Program> can find it in integration tests.
public partial class Program;