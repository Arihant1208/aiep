using Aiep.Application.Execution;
using Microsoft.Extensions.Options;

namespace Aiep.Worker;

/// <summary>
/// Periodically recovers runs whose worker lease expired -- the safety net behind the delivery contract (ADR-0005,
/// task-lifecycle.md). Returns them to the queue, or dead-letters them when attempts are exhausted.
/// </summary>
public sealed class ReconcilerService(
    IRunQueue queue, IOptions<WorkerOptions> options, ILogger<ReconcilerService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ReconcileInterval);
        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                var recovered = await queue.ReconcileExpiredLeasesAsync(_options.ReconcileBatch, stoppingToken);
                if (recovered > 0)
                {
                    logger.LogWarning("Reconciled {Count} run(s) with expired leases", recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lease reconciliation failed");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
