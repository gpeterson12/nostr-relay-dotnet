using Microsoft.AspNetCore.Mvc;
using NostrRelay.Core;
using NostrRelay.Server.Metrics;
using NostrRelay.Server.Subscriptions;
using NostrRelay.Storage.Abstractions;

namespace NostrRelay.Server.Controllers;

// Section 4.4: liveness/readiness and Prometheus scraping. Grouped in one controller since
// both are operational endpoints with no protocol overlap with the relay's "/" endpoint,
// unlike that one there's no content negotiation here, just two independent routes.
[ApiController]
public sealed class DiagnosticsController(
    IEventStore store,
    RelayMetrics metrics,
    ConnectionRegistry connections,
    SubscriptionRegistry subscriptions)
    : ControllerBase
{
    // Enumerates at most one row via the existing IEventStore.QueryAsync rather than
    // calling CountAsync: a COUNT(*) scan (even an index-backed one) does unnecessary work
    // for a probe that's only meant to prove the database is reachable, and that cost grows
    // with table size. A single-row query with Limit = 1 still exercises storage end to end,
    // so an unreachable database still fails this check, without paying for a full scan on
    // every poll. CancellationToken.None is intentional, kept as in the original minimal-API
    // handler, so the probe still reports the database's true reachability even if the
    // orchestrator's health-check client disconnects mid-request.
    [HttpGet("/health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            await foreach (NostrEvent _ in store.QueryAsync([new NostrFilter { Limit = 1 }], CancellationToken.None))
                break;

            return Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "storage unreachable");
        }
    }

    // Covers connection/subscription/event counts for now; query latency histograms and
    // storage size are deliberately not here yet, see PrometheusTextFormatter's doc comment
    // for why.
    [HttpGet("/metrics")]
    public ContentResult Metrics()
    {
        var text = PrometheusTextFormatter.Format(metrics, connections, subscriptions);
        return Content(text, "text/plain; version=0.0.4");
    }
}