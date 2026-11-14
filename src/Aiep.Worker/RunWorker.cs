using Aiep.Application.Execution;
using Microsoft.Extensions.Options;

namespace Aiep.Worker;

/// <summary>
/// The worker loop (ADR-0005): claim the next run, execute and settle it, repeat. When the queue is empty it waits for an
/// enqueue signal (LISTEN/NOTIFY) with the poll interval as a fallback. Run ids may appear in logs; prompts, outputs and
/// errors never do (CLAUDE.md, ADR-0012).
/// </summary>
public sealed class RunWorker(
    IRunQueue queue, RunExecutor executor, IOptions<WorkerOptions> options, ILogger<RunWorker> logger)
    : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Run worker {WorkerId} started", _options.WorkerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            ClaimedRun? run;
            try
            {
                run = await queue.ClaimAsync(_options.WorkerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Claiming a run failed; backing off");
                await WaitAsync(stoppingToken);
                continue;
            }

            if (run is null)
            {
                try
                {
                    await queue.WaitForEnqueueSignalAsync(_options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Enqueue wait failed; falling back to polling");
                    await WaitAsync(stoppingToken);
                }

                continue;
            }

            try
            {
                var outcome = await executor.ExecuteAsync(run, stoppingToken);
                logger.LogInformation("Run {RunId} settled as {Outcome}", run.RunId, outcome?.ToString() ?? "abandoned");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Executing run {RunId} failed", run.RunId);
            }
        }

        logger.LogInformation("Run worker {WorkerId} stopping", _options.WorkerId);
    }

    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.PollInterval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
