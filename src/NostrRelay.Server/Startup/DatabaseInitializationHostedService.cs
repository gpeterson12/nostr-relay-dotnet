using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Server.Startup;

/// <summary>
/// Runs <see cref="IEventStore.InitializeAsync"/> (schema creation/migration) during the
/// host's "Starting" phase rather than as an ordinary hosted service's <c>StartAsync</c>.
///
/// This distinction matters: the generic host calls <c>StartingAsync</c> on every
/// registered <see cref="IHostedLifecycleService"/>, sequentially, to completion, before it
/// calls <c>StartAsync</c> on any hosted service at all. Kestrel itself is started by an
/// internal hosted service that only implements the plain <see cref="IHostedService"/>
/// interface, so it participates in that later <c>StartAsync</c> phase, not the earlier
/// <c>StartingAsync</c> one. Implementing <see cref="IHostedLifecycleService"/> here, and
/// doing the real work in <see cref="StartingAsync"/>, is what guarantees storage is
/// initialized before Kestrel begins accepting connections, and also before any other
/// ordinary hosted service (e.g. the NIP-40 expiration sweep) starts running against it.
/// Registering this as a plain <c>IHostedService</c>/<c>BackgroundService</c> instead would
/// not provide that guarantee, since its <c>StartAsync</c> would only be ordered relative
/// to other services by DI registration order, not guaranteed to precede Kestrel.
///
/// Registered via the ordinary <c>AddHostedService&lt;T&gt;()</c> extension in
/// <c>Program.cs</c>: the host detects the additional interface itself, no special
/// registration call is needed.
/// </summary>
public sealed class DatabaseInitializationHostedService(IEventStore eventStore) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken) =>
        await eventStore.InitializeAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}