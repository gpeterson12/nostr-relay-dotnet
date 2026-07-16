using System.Text;
using NostrRelay.Server.Subscriptions;

namespace NostrRelay.Server.Metrics;

/// <summary>
/// Hand-written Prometheus text exposition (format version 0.0.4: "# HELP", "# TYPE", then
/// "metric_name{labels} value" lines). Deliberately not using the OpenTelemetry Prometheus
/// exporter package: this project's metrics needs right now are a handful of counters and
/// gauges, not worth an extra dependency. The exposition format itself
/// is a small, stable, well-documented text format, safe to hand-write directly.
/// </summary>
public static class PrometheusTextFormatter
{
    public static string Format(RelayMetrics metrics, ConnectionRegistry connections, SubscriptionRegistry subscriptions)
    {
        var sb = new StringBuilder();

        AppendGauge(sb, "nostr_relay_connections_active", "Number of currently open WebSocket connections.", connections.Count);
        AppendCounter(sb, "nostr_relay_connections_opened_total", "Total WebSocket connections accepted since startup.", metrics.ConnectionsOpenedTotal);
        AppendGauge(sb, "nostr_relay_subscriptions_active", "Number of currently active REQ subscriptions across all connections.", subscriptions.TotalSubscriptionCount);
        AppendCounter(sb, "nostr_relay_events_ingested_total", "Total events that passed validation and were handed to storage.", metrics.EventsIngestedTotal);

        sb.Append("# HELP nostr_relay_events_rejected_total Total events rejected, by reason prefix.\n");
        sb.Append("# TYPE nostr_relay_events_rejected_total counter\n");
        if (metrics.EventsRejectedByReason.Count == 0)
        {
            sb.Append("nostr_relay_events_rejected_total{reason=\"none\"} 0\n");
        }
        else
        {
            foreach (var (reason, count) in metrics.EventsRejectedByReason)
                sb.Append($"nostr_relay_events_rejected_total{{reason=\"{EscapeLabelValue(reason)}\"}} {count}\n");
        }

        return sb.ToString();
    }

    private static void AppendGauge(StringBuilder sb, string name, string help, long value)
    {
        sb.Append($"# HELP {name} {help}\n");
        sb.Append($"# TYPE {name} gauge\n");
        sb.Append($"{name} {value}\n");
    }

    private static void AppendCounter(StringBuilder sb, string name, string help, long value)
    {
        sb.Append($"# HELP {name} {help}\n");
        sb.Append($"# TYPE {name} counter\n");
        sb.Append($"{name} {value}\n");
    }

    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", @"\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
