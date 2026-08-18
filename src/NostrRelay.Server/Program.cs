using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

// Section 5.6's "Storage" section, bound to a real options type alongside Limits/Policy/
// ExpirationSweep below.
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

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
        // manually constructing a PooledDbContextFactory<T>. PostgresEventStore takes both,
        // plus the IOptions<StorageOptions> configured above, as constructor parameters, so
        // it needs no special construction step of its own, ordinary AddSingleton below.
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

// NIP-11 document and its serializer options depend only on configuration fixed at
// startup, so both are resolved through DI as singletons rather than built as
// Program.cs locals captured by the "/" delegate's closure. This keeps the delegate's
// dependencies visible in its parameter list and makes RelayInfoDocument available to
// any other consumer that might need it later (an admin endpoint, for instance).
builder.Services.AddSingleton(sp =>
{
    RelayLimitsOptions limits = sp.GetRequiredService<IOptions<RelayLimitsOptions>>().Value;
    RelayPolicyOptions policy = sp.GetRequiredService<IOptions<RelayPolicyOptions>>().Value;
    return RelayInfoDocumentFactory.Create(sp.GetRequiredService<IConfiguration>(), limits, policy);
});

builder.Services.AddSingleton(new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});

// Section 6/4.4 endpoints (root NIP-11/WebSocket negotiation, /health, /metrics) live in
// Controllers/ now rather than as minimal-API delegates, so AddControllers/MapControllers
// replace the old app.Map/app.MapGet calls below.
builder.Services.AddControllers();

// Schema must exist before any request is handled. DatabaseInitializationHostedService
// implements IHostedLifecycleService, so the host runs its StartingAsync to completion
// before starting Kestrel (or any other hosted service), see that class's doc comment for
// why that guarantee doesn't hold for an ordinary IHostedService. It resolves IEventStore
// generically, so this works identically regardless of which provider branch above ran.
builder.Services.AddHostedService<DatabaseInitializationHostedService>();

WebApplication app = builder.Build();

app.UseWebSockets();

// Root NIP-11/WebSocket negotiation, /health, and /metrics are now RelayController and
// DiagnosticsController in Controllers/, see those files for the per-endpoint comments
// that used to live here.
app.MapControllers();

app.Run();

// Exposes the otherwise-implicit top-level-statements Program class so
// WebApplicationFactory<Program> can find it in integration tests.
public partial class Program;