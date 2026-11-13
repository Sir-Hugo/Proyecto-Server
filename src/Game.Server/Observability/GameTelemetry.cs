using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Game.Server.Observability;

public sealed class GameTelemetry : IDisposable
{
    public const string MeterName = "Game.Server";
    public const string ActivitySourceName = "Game.Server.Operations";

    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly Counter<long> roundPlansProcessed;
    private readonly Histogram<double> roundPlanProcessingDuration;
    private readonly Histogram<double> roundResolutionDuration;
    private readonly Counter<long> failures;
    private long activeMatches;
    private long activeConnections;

    public static ActivitySource Activities { get; } = new(ActivitySourceName);

    public GameTelemetry()
    {
        roundPlansProcessed = meter.CreateCounter<long>("game.round_plans.processed", "{plan}",
            "New round plans accepted and persisted; idempotent retries are excluded.");
        roundPlanProcessingDuration = meter.CreateHistogram<double>("game.round_plan.processing.duration", "ms",
            "Server-side validation and transactional persistence time for a newly accepted plan; excludes SignalR delivery.");
        roundResolutionDuration = meter.CreateHistogram<double>("game.round.resolution.duration", "ms",
            "Synchronous domain resolution time after both plans are available; excludes persistence and delivery.");
        failures = meter.CreateCounter<long>("game.failures", "{failure}", "Relevant bounded server failures by operation and category.");
        meter.CreateObservableGauge("game.matches.active", () => Interlocked.Read(ref activeMatches), "{match}",
            "Active matches observed in PostgreSQL by the background collector.");
        meter.CreateObservableGauge("game.signalr.connections.active", () => Interlocked.Read(ref activeConnections), "{connection}",
            "Authenticated SignalR connections currently registered in this server replica.");
    }

    public void SetActiveMatches(long value) => Interlocked.Exchange(ref activeMatches, Math.Max(0, value));
    public void SetActiveConnections(long value) => Interlocked.Exchange(ref activeConnections, Math.Max(0, value));

    public void RecordRoundPlanProcessed(double processingMilliseconds)
    {
        roundPlansProcessed.Add(1);
        roundPlanProcessingDuration.Record(processingMilliseconds);
    }

    public void RecordRoundResolved(double resolutionMilliseconds) => roundResolutionDuration.Record(resolutionMilliseconds);

    public void RecordFailure(string operation, string category) => failures.Add(1,
        new KeyValuePair<string, object?>("operation", operation),
        new KeyValuePair<string, object?>("category", category));

    public void Dispose() => meter.Dispose();
}
