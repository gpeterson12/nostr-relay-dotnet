using Microsoft.Extensions.Options;
using NostrRelay.Server.Configuration;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Server.Expiration;

/// <summary>
/// NIP-40's background cleanup job: periodically calls <c>IEventStore.DeleteExpiredEventsAsync</c>
/// to reclaim storage from expired events. Deliberately not required for correctness,
/// SqliteEventStore/PostgresEventStore already exclude expired events from every
/// <c>QueryAsync</c>/<c>CountAsync</c> call regardless of whether this has run recently
/// (NIP-40: "Relays SHOULD NOT send expired events to clients, even if they are stored").
/// This service exists purely so expired rows don't accumulate indefinitely
/// ("Relays MAY NOT delete expired messages immediately... and MAY persist them
/// indefinitely" — this is the relay choosing not to persist them indefinitely).
/// </summary>
public sealed class ExpirationSweepService(
    IEventStore eventStore,
    IOptions<ExpirationSweepOptions> options,
    ILogger<ExpirationSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await eventStore.DeleteExpiredEventsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is not fatal: expired events remain correctly hidden from
                // queries regardless (that's enforced at query time, not by this job), so
                // log and try again on the next tick rather than crashing the host.
                logger.LogWarning(ex, "expiration sweep failed, will retry on the next interval");
            }
        }
    }
}
