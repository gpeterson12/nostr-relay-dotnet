using NostrRelay.Core;

namespace NostrRelay.Storage.Abstractions;

/// <summary>
/// Storage-agnostic contract for persisting and querying Nostr events (Section 5.2). All
/// Nostr-specific semantics live behind this interface: kind-category branching (Section
/// 3.3), filter matching (Section 3.4), and NIP-40 expiration all become implementation
/// details of whichever concrete store is wired up.
///
/// A single shared <c>EventStoreContractTests</c> abstract base class (Storage.Tests, added
/// once a second provider exists) exercises every implementation against this same
/// interface, proving behavioral parity between SQLite and Postgres rather than assuming it.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Persists <paramref name="evt"/> according to its kind category (Section 3.3). Callers
    /// should assume the event has already passed <see cref="Core.Validation.EventValidationPipeline"/>;
    /// this method is not responsible for re-validating id/signature.
    /// </summary>
    Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct);

    /// <summary>
    /// Streams events matching any of <paramref name="filters"/> (OR'd together, Section 3.4),
    /// most-recent-first. Each filter's own <c>Limit</c> caps how many of that filter's matches
    /// are returned; when multiple filters are supplied the caller is responsible for any
    /// overall response-size policy beyond the per-filter limits.
    /// </summary>
    IAsyncEnumerable<NostrEvent> QueryAsync(IReadOnlyList<NostrFilter> filters, CancellationToken ct);

    /// <summary>Count-optimized path for NIP-45: no row materialization, just a count.</summary>
    Task<long> CountAsync(NostrFilter filter, CancellationToken ct);

    /// <summary>Hard-deletes the given event ids (NIP-09 soft/hard delete handling is a
    /// server-layer policy decision; this method just removes rows).</summary>
    Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct);

    /// <summary>NIP-40 sweep: deletes all events whose <c>expires_at</c> has passed. Intended
    /// to be called periodically by a background <c>IHostedService</c> (Milestone 9).</summary>
    Task DeleteExpiredEventsAsync(CancellationToken ct);
}
