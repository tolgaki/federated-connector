using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FederatedConnector.Host.Telemetry;

/// <summary>
/// Traces and metrics for tool calls. Records tool, provider, collection, latency, result count, source status and
/// hashed tenant and user ids. Never records query text or document content.
/// </summary>
public sealed class ConnectorTelemetry : IDisposable
{
    public const string SourceName = "FederatedConnector";

    private readonly Meter _meter = new(SourceName);
    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;
    private readonly Histogram<long> _results;

    public ConnectorTelemetry()
    {
        _calls = _meter.CreateCounter<long>("connector.tool.calls");
        _duration = _meter.CreateHistogram<double>("connector.tool.duration", "ms");
        _results = _meter.CreateHistogram<long>("connector.tool.results");
    }

    public static ActivitySource ActivitySource { get; } = new(SourceName);

    public void Record(ToolCallRecord r)
    {
        var tags = new TagList
        {
            { "tool", r.Tool },
            { "provider", r.Provider },
            { "outcome", r.Outcome },
            { "collection", r.Collection ?? "*" },
        };
        if (r.SourceStatus is { } status)
        {
            tags.Add("source_status", status);
        }

        _calls.Add(1, tags);
        _duration.Record(r.DurationMs, tags);
        if (r.ResultCount is { } count)
        {
            _results.Record(count, tags);
        }

        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("connector.tool", r.Tool);
            activity.SetTag("connector.provider", r.Provider);
            activity.SetTag("connector.collection", r.Collection);
            activity.SetTag("connector.outcome", r.Outcome);
            activity.SetTag("connector.result_count", r.ResultCount);
            activity.SetTag("connector.source_status", r.SourceStatus);
            activity.SetTag("connector.tenant_hash", r.TenantHash);
            activity.SetTag("connector.user_hash", r.UserHash);
        }
    }

    public void Dispose() => _meter.Dispose();
}

public sealed record ToolCallRecord(
    string Tool,
    string Provider,
    string? Collection,
    string Outcome,
    double DurationMs,
    int? ResultCount,
    int? SourceStatus,
    string TenantHash,
    string UserHash);
