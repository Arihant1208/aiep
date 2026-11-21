using Aiep.Application.Execution;
using Microsoft.Extensions.Options;

namespace Aiep.Worker;

/// <summary>
/// Retention cleanup (Phase 3, ADR-0015, Spec §15): periodically purge expired idempotency keys and terminal runs past
/// the retention window, with their attempts. Cross-tenant, through the queue's purge function.
/// </summary>
public sealed class MaintenanceService(
    IRunQueue queue, IOptions<WorkerOptions> options, ILogger<MaintenanceService> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PurgeInterval);
        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                var purged = await queue.PurgeExpiredAsync(_options.RetentionWindow, _options.PurgeBatch, stoppingToken);
                if (purged.RunsDeleted > 0 || purged.IdempotencyKeysDeleted > 0)
                {
                    logger.LogInformation(
                        "Retention sweep removed {Runs} run(s) and {Keys} idempotency key(s)",
                        purged.RunsDeleted, purged.IdempotencyKeysDeleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention sweep failed");
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
