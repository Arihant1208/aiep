namespace Aiep.Worker;

/// <summary>
/// Claims and executes queued runs. Phase 0 placeholder: the loop exists so the worker process,
/// its identity, and its telemetry can be deployed and observed before execution logic lands in Phase 2.
/// </summary>
public sealed class RunProcessor(ILogger<RunProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Run processor started; run claiming is not implemented yet");

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Phase 2: claim a run lease, execute its handler, persist the outcome (ADR-0005).
            logger.LogDebug("Run processor poll tick");
        }
    }
}
