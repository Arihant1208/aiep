using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aiep.Application.Execution;

/// <summary>
/// Activity source and meter for run execution (ADR-0012; names match the <c>Aiep.*</c> pattern the hosts collect).
/// Hygiene is a hard rule: spans and metrics carry only low-cardinality, non-sensitive attributes -- never prompts,
/// inputs, outputs, tenant ids or run ids.
/// </summary>
public static class ExecutionTelemetry
{
    public const string SourceName = "Aiep.Execution";
    public const string MeterName = "Aiep.Runs";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Runs settled, tagged by terminal status and (for failures) error category.</summary>
    public static readonly Counter<long> RunsSettled =
        Meter.CreateCounter<long>("aiep.runs.settled", unit: "{run}", description: "Runs settled by the worker.");

    /// <summary>Wall-clock duration of one execution attempt.</summary>
    public static readonly Histogram<double> ExecutionDuration =
        Meter.CreateHistogram<double>("aiep.runs.execution.duration", unit: "ms", description: "Attempt execution time.");

    /// <summary>Model-provider call latency, as reported by the provider.</summary>
    public static readonly Histogram<double> ProviderLatency =
        Meter.CreateHistogram<double>("aiep.runs.provider.latency", unit: "ms", description: "Provider call latency.");
}
