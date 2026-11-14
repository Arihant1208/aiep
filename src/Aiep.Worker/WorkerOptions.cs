namespace Aiep.Worker;

/// <summary>Worker loop configuration (bound from the <c>Worker</c> configuration section).</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>The lease owner written to claimed runs. Unique per process so leases are attributable.</summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>How long to wait for an enqueue signal before polling again when the queue is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the reconciler sweeps for expired leases.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum expired leases recovered per reconcile sweep.</summary>
    public int ReconcileBatch { get; set; } = 100;
}
