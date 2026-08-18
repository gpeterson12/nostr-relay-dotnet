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
    ///  Ensures that the database is created and all migrations have been run.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

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

    /// <summary>Hard-deletes the given event ids unconditionally, no ownership check. Used
    /// by administrative/CLI tooling, not by NIP-09 processing, see
    /// <see cref="DeleteEventsAuthoredByAsync"/> for that.</summary>
    Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct);

    /// <summary>
    /// NIP-09: deletes each event in <paramref name="eventIds"/> only if it is currently
    /// stored with pubkey equal to <paramref name="authorPubkey"/>, and is not itself a
    /// kind-5 deletion request event (NIP-09: "Publishing a deletion request event
    /// against a deletion request has no effect"). Ids that don't exist, belong to a
    /// different pubkey, or are kind 5 are silently skipped, not an error, mirroring how
    /// relays generally cannot fully validate deletion requests in the first place.
    /// </summary>
    Task DeleteEventsAuthoredByAsync(IEnumerable<string> eventIds, string authorPubkey, CancellationToken ct);

    /// <summary>
    /// NIP-09 "a" tag handling: deletes the currently-stored addressable/replaceable event
    /// for the given (pubkey, kind, d-tag) coordinate, but only if its <c>created_at</c> is
    /// at or before <paramref name="upToCreatedAt"/> (the deletion request's own
    /// <c>created_at</c>). A no-op if no matching row is stored, or the stored row is newer
    /// than the deletion request, a legitimate update racing ahead of an older deletion
    /// request should not be undone by it.
    /// </summary>
    Task DeleteAddressableEventAsync(string pubkey, int kind, string dTag, long upToCreatedAt, CancellationToken ct);

    /// <summary>NIP-40 sweep: deletes all events whose <c>expires_at</c> has passed. Intended
    /// to be called periodically by a background <c>IHostedService</c> (Milestone 9). Note
    /// that <see cref="QueryAsync"/> and <see cref="CountAsync"/> already exclude expired
    /// events regardless of whether this sweep has run yet (NIP-40: "Relays SHOULD NOT
    /// send expired events to clients, even if they are stored"), this method is about
    /// reclaiming storage, not about correctness of query results.</summary>
    Task DeleteExpiredEventsAsync(CancellationToken ct);
}