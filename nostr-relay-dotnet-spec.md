# NostrRelay.NET — Technical Specification

## 1. Project Overview

### 1.1 What This Is

A production-grade Nostr relay implementation written in C# / .NET 10. A relay is a WebSocket server that stores signed, cryptographically-verifiable events and serves them to Nostr clients based on subscription filters. This project fills a gap in the Nostr ecosystem: at time of writing, no widely-used C#/.NET relay implementation exists, despite active implementations in Rust, Go, TypeScript, Clojure, and C++.

### 1.2 Goals

- Fully spec-compliant implementation of NIP-01 (core protocol) plus a curated set of extension NIPs.
- Demonstrate senior-level .NET backend engineering: clean architecture, concurrency correctness, measurable performance, dual-datastore support, comprehensive testing, and observability.
- Be genuinely usable: able to accept connections from real Nostr clients (Damus, Amethyst, Primal, Coracle, etc.) and interoperate correctly with the live network.
- Serve as a portfolio piece showcasing distributed-systems thinking and applied cryptography, not just CRUD-over-HTTP skills.

### 1.3 Non-Goals (v1)

- No built-in payment/paid-relay logic (NIP-42 auth and allowlisting are in scope; Lightning paywalls are not).
- No relay-to-relay federation/syncing protocol beyond what NIPs define.
- No bundled client or UI, this is server-only. A minimal static NIP-11 info page is acceptable but not a chat UI.
- No horizontal multi-node clustering in v1 (single-node, but architecture should not preclude it later — see Section 9).

---

## 2. Background: Nostr Protocol Primer

Nostr ("Notes and Other Stuff Transmitted by Relays") is a decentralized protocol. There is no blockchain, no token, and no central server. Identity is a secp256k1 keypair. Content is a signed JSON "event." Clients connect to one or more relays over WebSocket to publish and query events. Relays are intentionally simple ("dumb pipes with storage") so that innovation happens in clients, and no single relay is a single point of failure or censorship for a user, since the same signed events can be re-published to any relay.

### 2.1 Core Concepts

- **Keypair**: secp256k1 private/public key pair. Public key (32-byte x-only, per BIP-340) is the user's identity.
- **Event**: the atomic unit of data. JSON object containing id, pubkey, created_at, kind, tags, content, sig.
- **Kind**: an integer denoting event type/semantics (0 = metadata, 1 = short text note, 3 = contacts, 4 = encrypted DM, 5 = deletion request, etc.). Kind ranges have defined behaviors (regular, replaceable, ephemeral, addressable — see 3.3).
- **Relay**: WebSocket server that stores and serves events.
- **Client**: application that connects to relays to read/write events on behalf of a user.
- **Filter**: a query object clients send to request matching events (by ids, authors, kinds, tags, time range, limit).
- **Subscription**: a client-assigned identifier for an open, live-updating filter. The relay must send matching events to that subscription both for historical (stored) matches and future (newly published) events until the subscription is closed.

### 2.2 Message Flow

All communication happens over a single persistent WebSocket connection per client, using JSON arrays as messages.

**Client → Relay:**

- `["EVENT", <event JSON>]` — publish an event.
- `["REQ", <subscription_id>, <filter1>, <filter2>, ...]` — open/replace a subscription with one or more filters (OR'd together).
- `["CLOSE", <subscription_id>]` — close a subscription.
- `["AUTH", <event JSON>]` — respond to relay-issued auth challenge (NIP-42).
- `["COUNT", <subscription_id>, <filter>]` — request a count instead of full events (NIP-45).

**Relay → Client:**

- `["EVENT", <subscription_id>, <event JSON>]` — deliver a matching event for a subscription.
- `["OK", <event_id>, <true|false>, <message>]` — acknowledge an EVENT publish, with success/failure and a machine-readable reason prefix (e.g., `"blocked: ..."`, `"invalid: ..."`, `"rate-limited: ..."`, `"duplicate: ..."`).
- `["EOSE", <subscription_id>]` — "end of stored events": signals the client that all historical matches have been sent; subsequent EVENT messages for this subscription are live.
- `["CLOSED", <subscription_id>, <message>]` — relay unilaterally closed a subscription (e.g., invalid filter, or policy).
- `["NOTICE", <message>]` — human-readable message for debugging/errors.
- `["AUTH", <challenge>]` — relay asks client to authenticate (NIP-42).
- `["COUNT", <subscription_id>, {"count": N}]` — response to a COUNT request.

### 2.3 Event Validation Rules (Critical Path)

Every incoming EVENT must be validated in this order before storage:

1. **Structural validation**: required fields present, correct types, `kind` is a non-negative integer, `created_at` is a reasonable Unix timestamp, `tags` is an array of string arrays.
2. **ID verification**: the event `id` must equal `SHA256(serialized_event)` where the serialization is the canonical form: `[0, pubkey, created_at, kind, tags, content]` as a JSON array with no whitespace, UTF-8 encoded. Reject if mismatched (prevents tampering).
3. **Signature verification**: `sig` must be a valid BIP-340 Schnorr signature over the event `id` (as a 32-byte message), verified against `pubkey` (32-byte x-only public key). This is the cryptographic core of the whole system — reject on any failure.
4. **Policy validation**: size limits, rate limits, allowlist/blocklist checks (pubkey and/or kind based), timestamp sanity (not too far in the future/past, configurable).
5. **Kind-specific handling**: see Section 3.3 (replaceable/ephemeral/addressable logic) before persisting.

This validation pipeline is the single most important piece of "showcase" code in the project — it should be implemented as a clean, testable, composable pipeline (e.g., chain of `IEventValidator` steps), not a single monolithic method.

---

## 3. Functional Requirements

### 3.1 NIPs to Implement — v1 (MVP)

| NIP    | Name                       | Why                                                                                                                                                |
| ------ | -------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| NIP-01 | Basic protocol flow        | Mandatory, this is the protocol itself                                                                                                             |
| NIP-11 | Relay information document | Required for any client to identify/configure against your relay (served as JSON over HTTP on the same port with `Accept: application/nostr+json`) |
| NIP-09 | Event deletion             | Small, high value, tests soft-delete semantics                                                                                                     |
| NIP-40 | Expiration timestamp       | Small, demonstrates TTL/background cleanup design                                                                                                  |
| NIP-70 | Protected events           | Simple boolean tag check, demonstrates policy layering                                                                                             |

### 3.2 NIPs to Implement — v1.1 (Stretch, post-MVP)

| NIP    | Name                                | Why                                                                                                                 |
| ------ | ----------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| NIP-42 | Authentication of clients to relays | Demonstrates challenge/response auth over WebSocket, session state                                                  |
| NIP-45 | Event counts                        | Demonstrates a separate optimized query path (count-only, no row materialization)                                   |
| NIP-50 | Search capability                   | Demonstrates full-text search integration (Postgres `tsvector` / SQLite FTS5)                                       |
| NIP-13 | Proof of Work                       | Demonstrates a pluggable spam-resistance mechanism (leading-zero-bits difficulty check on event id)                 |
| NIP-65 | Relay list metadata                 | Small, standard kind-10002 handling, no special relay logic beyond storing/serving it as a normal replaceable event |

Explicitly out of scope for this repo: NIP-04/NIP-44 (DM encryption — client-side concern, relay just stores opaque content), NIP-57 (zaps, requires Lightning infra), NIP-05 (DNS identifier, unrelated to relay storage/query logic).

### 3.3 Event Kind Categories (NIP-01)

The relay must implement different storage/query semantics per kind range:

- **Regular events** (kind 1000–9999, and kind 1, 2, 4-44, minus special ranges): stored permanently, all versions kept, queryable by id/time/etc. Example: kind 1 (text note).
- **Replaceable events** (kind 0, 3, 10000–19999): only the _latest_ event per `(pubkey, kind)` pair is retained. On insert, if a newer or equal `created_at` already exists for that pubkey+kind, the incoming event is discarded (with an `OK false "replaced: ..."` or accepted-but-superseded response per spec nuance). On insert of a genuinely newer event, older ones are deleted. Example: kind 0 (profile metadata), kind 3 (contact list).
- **Ephemeral events** (kind 20000–29999): never stored at all. Validated, then broadcast live to matching subscriptions, then discarded. Example: kind 20001 (ephemeral chat typing indicator style events).
- **Addressable events** (kind 30000–39999): like replaceable, but keyed on `(pubkey, kind, d-tag-value)` instead of just `(pubkey, kind)`. Requires reading the `d` tag from the event. Example: kind 30023 (long-form article), which can be updated/versioned by identifier.

This four-way branching logic is a good candidate for a strategy pattern (`IEventPersistenceStrategy` per kind category) rather than if/else sprawl.

### 3.4 Filter Matching Semantics

A filter (in a REQ) can include:

- `ids`: array of event id prefixes to match.
- `authors`: array of pubkey prefixes to match.
- `kinds`: array of integer kinds.
- `#<single-letter-tag>`: e.g. `#e`, `#p` — array of tag values to match against that tag.
- `since` / `until`: Unix timestamp bounds (inclusive).
- `limit`: max number of events to return for the initial (historical) batch, most-recent-first.

Multiple filters in one REQ are OR'd. Within a single filter, all specified conditions are AND'd. This is the query engine's core logic and should be implemented so it works identically whether querying SQLite or Postgres (i.e., the filter-to-SQL translation is a shared or parallel-but-tested component).

### 3.5 Subscription Lifecycle

- A REQ with a new subscription id opens a subscription: relay first sends all matching stored events (respecting `limit`, most recent first), then sends `EOSE`, then continues sending any newly published matching events live until CLOSE or disconnect.
- A REQ reusing an existing subscription id on the same connection replaces that subscription (spec-defined behavior, common in practice for client-side filter updates).
- Each connection can have multiple concurrent subscriptions (practical cap should be configurable, e.g., default max 20 per connection).
- On disconnect, all subscriptions for that connection are cleaned up immediately (no leaks).

This is the most concurrency-sensitive part of the system: every stored connection's active subscriptions must be checked against every newly published event, in real time, without blocking event ingestion and without missing events under concurrent publish/subscribe. See Section 5.3 for the design approach.

---

## 4. Non-Functional Requirements

### 4.1 Performance Targets (to benchmark and publish in README)

- Sustain **1,000+ concurrent WebSocket connections** on a single low-cost VPS (e.g., 2 vCPU / 2GB RAM) without degraded latency.
- **Event ingestion**: p99 validation+persist latency under 15ms for SQLite (WAL mode), under 25ms for Postgres, on typical event sizes (<2KB).
- **Subscription fan-out**: a published event should reach all matching live subscribers within single-digit milliseconds of persistence, independent of total subscriber count up to at least 10,000 active subscriptions.
- **Signature verification throughput**: benchmark raw Schnorr verifications/sec on target hardware (informational, not user-facing latency, but a good BenchmarkDotNet number for the README).

### 4.2 Reliability

- No event loss: once a relay sends `OK true` for an event, it must be durably persisted (respect storage engine's fsync/WAL guarantees) or, for ephemeral kinds, guaranteed to have been broadcast to all currently-subscribed matching connections before returning.
- Graceful shutdown: in-flight WebSocket messages flushed, new connections rejected, existing connections closed with a clean WebSocket close frame, within a configurable drain timeout.
- No single slow/malicious client should be able to degrade service for others (backpressure and per-connection resource caps — see 5.4).

### 4.3 Security / Abuse Resistance

- Reject malformed/oversized payloads before JSON deserialization where possible (raw byte length cap on incoming WebSocket frames).
- Rate limit per connection (and optionally per pubkey) for both EVENT publishes and REQ subscriptions, using a token bucket.
- Optional NIP-13 proof-of-work minimum difficulty, configurable, to deter spam without requiring identity.
- Optional pubkey allowlist/blocklist and kind blocklist, config-driven.
- Never trust client-supplied `created_at` for anything beyond the value itself; do not use it in server-side ordering-critical logic where wall-clock server time matters.

### 4.4 Observability

- Structured logging (`Microsoft.Extensions.Logging`, JSON console formatter) with correlation per-connection and per-event via `ILogger.BeginScope`.
- Prometheus-compatible `/metrics` endpoint: connection count, events ingested/sec, events rejected (by reason), active subscriptions, query latency histograms, storage size.
- Health check endpoint (`/health`) for container orchestration liveness/readiness probes.

---

## 5. Technical Architecture

### 5.1 Tech Stack

- **.NET 10**, C# 14.
- **ASP.NET Core** minimal hosting model for the WebSocket endpoint and HTTP (NIP-11, health, metrics) endpoints on the same port (protocol negotiation via `Upgrade` header for WS vs. `Accept: application/nostr+json` for NIP-11, standard Nostr relay convention).
- **Native WebSockets** (`System.Net.WebSockets`) rather than SignalR, since SignalR's abstractions (hubs, its own protocol framing) fight against implementing a specific external wire protocol. Raw `WebSocket` gives full control over the exact JSON array framing Nostr requires.
- **System.Threading.Channels** for the internal event bus (publish → subscription fan-out pipeline). Avoids external message broker dependency for v1 while keeping the design swappable (see 9.2).
- **Cryptography**: `NBitcoin.Secp256k1` (or `NSec.Cryptography` if it has BIP-340 Schnorr support at implementation time — verify current package support) for Schnorr signature verification. Do not hand-roll elliptic curve math.
- **Storage**: dual-provider, see 5.2.
- **Serialization**: `System.Text.Json` with a hand-written converter for the Nostr event array wire format (since messages are heterogeneous JSON arrays, not simple objects, default STJ object mapping won't apply directly to the outer message envelope).
- **Logging**: `Microsoft.Extensions.Logging` with JSON console output (`AddJsonConsole()`), using `ILogger.BeginScope` to attach per-connection and per-event correlation properties (e.g., `ConnectionId`) across the async WebSocket read/write loops. No third-party logging dependency.
- **Testing**: xUnit, `WebApplicationFactory` for integration tests, a raw `ClientWebSocket` test harness for protocol-level tests, BenchmarkDotNet for performance tests.
- **Containerization**: Docker + docker-compose (relay + Postgres option + SQLite volume option).

### 5.2 Storage Abstraction (Dual Backend: SQLite + Postgres)

Define a storage-agnostic interface and keep all Nostr-specific query logic behind it:

```csharp
public interface IEventStore
{
    Task<PersistResult> SaveEventAsync(NostrEvent evt, CancellationToken ct);
    IAsyncEnumerable<NostrEvent> QueryAsync(NostrFilter[] filters, CancellationToken ct);
    Task<long> CountAsync(NostrFilter filter, CancellationToken ct);
    Task DeleteEventsAsync(IEnumerable<string> eventIds, CancellationToken ct);
    Task DeleteExpiredEventsAsync(CancellationToken ct); // NIP-40 sweep
}
```

Two implementations: `SqliteEventStore` and `PostgresEventStore`, selected via configuration (`Storage:Provider = "Sqlite" | "Postgres"`) and wired via DI at startup. Use **Dapper** (not full EF Core) for both, since the query shapes are simple, performance-sensitive, and you want explicit SQL control for the filter-matching queries, EF's LINQ-to-SQL translation adds risk of subtly inefficient generated SQL for dynamic filter combinations.

**Schema (conceptually identical in both engines):**

```
events
  id              TEXT/CHAR(64) PRIMARY KEY
  pubkey          TEXT/CHAR(64) NOT NULL
  created_at      INTEGER/BIGINT NOT NULL
  kind            INTEGER NOT NULL
  tags            TEXT/JSONB NOT NULL   -- raw tags array
  content         TEXT NOT NULL
  sig             TEXT/CHAR(128) NOT NULL
  expires_at      INTEGER/BIGINT NULL   -- NIP-40, indexed
  d_tag           TEXT NULL             -- extracted for addressable events, indexed

event_tags        -- normalized single-letter indexed tags for query performance
  event_id        TEXT/CHAR(64) NOT NULL
  tag_name         CHAR(1) NOT NULL
  tag_value        TEXT NOT NULL
```

Indexes needed on both engines: `(pubkey, kind)`, `(kind, created_at)`, `(created_at)`, `event_tags(tag_name, tag_value)`, and `(pubkey, kind, d_tag)` for addressable event upserts.

**Divergence points to handle explicitly:**

- Postgres: use `JSONB` for the raw `tags` column, `GIN` index for containment queries as an optional fast path; use `ON CONFLICT` for replaceable/addressable upserts.
- SQLite: use `TEXT` (JSON stored as string) for `tags`, rely on the normalized `event_tags` table for all tag filtering (SQLite has JSON1 extension but the normalized table is simpler and sufficiently fast at relay scale); use `INSERT OR REPLACE` / manual delete-then-insert for upserts; enable WAL mode for concurrent read/write.
- Both: connection pooling (Npgsql built-in pooling for Postgres; a small custom pool or `Microsoft.Data.Sqlite` connection-per-operation with WAL is fine for SQLite given its concurrency model).

This abstraction is the primary "clean architecture" showcase element. Write a shared contract test suite (`EventStoreContractTests`, abstract base class) that both `SqliteEventStore` and `PostgresEventStore` run against, proving behavioral parity, this is a strong, concrete demonstration of testing discipline.

### 5.3 Concurrency Model: Publish → Fan-out Pipeline

This is the architectural core of the relay and the best "senior engineer" showcase piece.

**Design**: In-process publish/subscribe using `System.Threading.Channels`, decoupling event ingestion from subscription matching and delivery.

1. Each WebSocket connection is handled by its own async loop (`Task`) reading frames and one loop writing frames, backed by a bounded `Channel<byte[]>` per connection for outbound messages (prevents one slow reader from blocking the writer or the publish pipeline).
2. A single in-memory `SubscriptionRegistry` (thread-safe, e.g., `ConcurrentDictionary<ConnectionId, ConcurrentDictionary<SubscriptionId, NostrFilter[]>>`) tracks all currently active subscriptions across all connections.
3. On successful validation+persistence of a new EVENT, the event is pushed into a central `Channel<NostrEvent>` ("the bus").
4. A pool of background consumer tasks reads from the bus channel, matches the event against the `SubscriptionRegistry` (in parallel where beneficial via `Parallel.ForEachAsync` for very large subscriber counts), and writes matching events into each matching connection's outbound channel.
5. Each connection's dedicated writer task drains its outbound channel and sends WebSocket frames, so slow clients only ever back up their own bounded channel (with a drop-oldest or disconnect policy once full, configurable) rather than blocking the bus or other clients.

This gives you: no shared-lock contention on the hot path beyond the registry's concurrent dictionary, natural backpressure isolation per connection, and a clean story for horizontal scale-out later (the bus channel is a natural seam to later replace with Redis Pub/Sub or NATS, see Section 9).

**Filter matching** must be efficient: precompute nothing fancier than a simple predicate evaluation per event, but ensure this is O(active subscriptions) per event, not O(active subscriptions × avg filter conditions) in a way that causes real slowdown, benchmark this explicitly at 10k+ subscriptions.

### 5.4 Backpressure and Resource Limits

- Bounded channels everywhere (inbound raw frame processing queue per connection, outbound message queue per connection) with explicit capacity and a documented drop/disconnect policy when full.
- Per-connection caps: max subscriptions, max filters per REQ, max concurrent unacked EVENT publishes.
- Global caps: max concurrent connections (reject new connections past this with a clean WebSocket close + reason), max events/sec ingestion (token bucket, returns `OK false "rate-limited: ..."`).
- All caps configurable via `appsettings.json` / environment variables, with sane production defaults documented in the README.

### 5.5 Project Structure

```
NostrRelay.sln
/src
  /NostrRelay.Core                # Domain: NostrEvent, NostrFilter, validation pipeline, kind-strategy logic, crypto verification wrapper
  /NostrRelay.Storage.Abstractions # IEventStore, PersistResult, shared contract test base (test project references this too)
  /NostrRelay.Storage.Sqlite
  /NostrRelay.Storage.Postgres
  /NostrRelay.Server              # ASP.NET Core host: WebSocket endpoint, message framing, subscription registry, bus, NIP-11 endpoint, metrics, health
  /NostrRelay.Cli                 # optional: small CLI for admin tasks (manual event delete, allowlist management, export/import)
/tests
  /NostrRelay.Core.Tests
  /NostrRelay.Storage.Tests       # contract tests run against both providers
  /NostrRelay.Server.IntegrationTests   # real WebSocket client hitting a spun-up TestServer, full protocol-level tests
  /NostrRelay.Benchmarks          # BenchmarkDotNet project
/docker
  docker-compose.yml              # relay + postgres profile, relay + sqlite-volume profile
  Dockerfile
README.md
ARCHITECTURE.md
```

### 5.6 Configuration

`appsettings.json` structure (illustrative):

```json
{
  "Relay": {
    "Name": "my-nostr-relay",
    "Description": "A .NET Nostr relay",
    "ContactPubkey": "...",
    "SupportedNips": [1, 9, 11, 13, 40, 42, 45, 50, 65, 70]
  },
  "Storage": {
    "Provider": "Sqlite",
    "ConnectionString": "Data Source=relay.db"
  },
  "Limits": {
    "MaxConnections": 5000,
    "MaxSubscriptionsPerConnection": 20,
    "MaxFiltersPerSubscription": 10,
    "MaxEventSizeBytes": 65536,
    "EventRateLimitPerMinute": 300,
    "MinProofOfWorkDifficulty": 0
  },
  "Policy": {
    "PubkeyAllowlist": [],
    "PubkeyBlocklist": [],
    "KindBlocklist": []
  }
}
```

---

## 6. API / Protocol Surface Summary

(For AI-assisted implementation: this section enumerates every message type the server must handle, as a checklist.)

**Inbound (must parse and handle):**

- `EVENT` — validate pipeline (Section 2.3) → persist per kind strategy (Section 3.3) → publish to bus → respond `OK`.
- `REQ` — parse filters → register subscription → query historical matches → stream `EVENT`s → send `EOSE`.
- `CLOSE` — deregister subscription.
- `AUTH` (v1.1) — verify challenge-response event → mark connection authenticated.
- `COUNT` (v1.1) — parse filter → run count-optimized query → respond `COUNT`.

**Outbound (must be able to send):**

- `EVENT`, `OK`, `EOSE`, `CLOSED`, `NOTICE`, `AUTH` (challenge), `COUNT` (response).

**HTTP (non-WebSocket, same port):**

- `GET /` with `Accept: application/nostr+json` → NIP-11 relay info document.
- `GET /health` → liveness/readiness.
- `GET /metrics` → Prometheus exposition format.

---

## 7. Testing Strategy

- **Unit tests** (`NostrRelay.Core.Tests`): event ID computation, Schnorr signature verification (valid + tampered + malformed inputs), filter matching logic against a broad matrix of filter/event combinations, kind-strategy branching (replaceable/ephemeral/addressable edge cases including simultaneous-timestamp collisions).
- **Contract tests** (`NostrRelay.Storage.Tests`): a single abstract test suite (e.g., `EventStoreContractTests<T>`) exercising `IEventStore`, run once against SQLite and once against Postgres (via Testcontainers for Postgres in CI), proving both implementations behave identically for save/query/delete/expire/replace/addressable semantics.
- **Integration/protocol tests** (`NostrRelay.Server.IntegrationTests`): spin up the server in-memory (`WebApplicationFactory`), connect with a real `ClientWebSocket`, walk through full protocol flows: publish → subscribe → receive, EOSE ordering correctness, CLOSE cleanup, multiple concurrent subscriptions, replaceable event supersession, expiration sweep.
- **Load/benchmark tests** (`NostrRelay.Benchmarks`): BenchmarkDotNet micro-benchmarks for signature verification and filter matching; a separate load-test script (k6 or a custom `ClientWebSocket`-based tool) for the concurrent-connection and throughput targets in Section 4.1, with results checked into the README.
- **Interop validation** (manual, pre-release): deploy to a public endpoint, connect at least one real client (Damus or Amethyst) and confirm publish/subscribe works against your relay from the live network.

---

## 8. Milestones (Suggested Build Order)

1. **Domain core**: `NostrEvent`, `NostrFilter`, JSON wire-format converters, ID computation, Schnorr verification wrapper, full validation pipeline with unit tests. No networking yet.
2. **Storage**: `IEventStore` interface, SQLite implementation first (simpler), contract test suite passing against it.
3. **Minimal WebSocket server**: accept connections, parse EVENT/REQ/CLOSE, wire to SQLite store, no fan-out yet (naive: REQ triggers one-time historical query only, no live updates). Get a real client to connect and see historical events — first real milestone.
4. **Live fan-out**: implement the Channels-based bus and SubscriptionRegistry from Section 5.3. Now live publish/subscribe works end-to-end.
5. **Kind-strategy logic**: replaceable/ephemeral/addressable handling, with unit + integration tests for each.
6. **Postgres implementation**: second `IEventStore` implementation, contract tests passing against both.
7. **NIP-11, health, metrics endpoints.**
8. **Policy & limits layer**: rate limiting, allowlist/blocklist, size caps, backpressure tuning.
9. **NIP-09 deletion, NIP-40 expiration** (with background sweep job, e.g., `IHostedService` timer).
10. **NIP-70 protected events.**
11. **Load testing and performance tuning against Section 4.1 targets; write up results.**
12. **Docker/docker-compose packaging, README, ARCHITECTURE.md, deploy to a real public endpoint, validate against a real client.**
13. **(Stretch) NIP-42 auth, NIP-45 count, NIP-13 PoW, NIP-50 search, NIP-65.**

---

## 9. Future Directions (Explicitly Not v1, But Design Should Not Preclude)

### 9.1 Horizontal Scale-Out

Replace the in-process `Channel<NostrEvent>` bus with Redis Pub/Sub or NATS so multiple relay instances behind a load balancer share a single logical event stream, allowing any instance to deliver events published to any other instance. The `IEventStore` abstraction already supports a shared Postgres backend for this.

### 9.2 Alternative Storage Engines

The `IEventStore` interface allows adding a high-performance embedded engine (e.g., LMDB via a P/Invoke wrapper, mirroring strfry's approach) as a third provider without touching server logic.

### 9.3 Paid Relay / Lightning Integration

NIP-42 auth plus a Lightning node connection (LND gRPC or LNURL) could gate write access behind a small sats fee, a natural extension once the core is stable.

### 9.4 Multi-Region Read Replicas

Given the Postgres backend, standard read-replica patterns apply for geographically distributed read-heavy deployments.

---

## 10. README Deliverables Checklist

For the finished repository, the README should include:

- Clear description, badges (build status, license).
- Architecture diagram (connection lifecycle + publish/fan-out pipeline).
- Quickstart (docker-compose up, connect with a client).
- Supported NIPs table.
- Configuration reference.
- Benchmark results table (connections sustained, ingestion latency percentiles, fan-out latency) with the hardware spec used.
- Testing instructions (unit / contract / integration / load).
- A short "Design Philosophy" section positioning this project relative to existing prior art in the C#/.NET Nostr space, most notably NNostr (Kukks/NNostr), a combined client/relay library tightly coupled to BTCPay Server for payment gating (per-event sats fees, admin configuration via DM commands) and built on Postgres only. This project makes a deliberate, different set of architectural choices: general-purpose (no payment coupling), dual-backend (SQLite for embedded/personal use, Postgres for scale), a documented Channels-based concurrency architecture for the publish/subscribe fan-out pipeline, a contract-tested storage abstraction proving behavioral parity across both providers, and published performance benchmarks. Frame this as "informed by existing prior art, built with different architectural priorities," not as filling an empty gap, other C#/.NET Nostr projects may exist beyond the ones referenced here, so avoid claims of uniqueness that a reader could easily disprove.
