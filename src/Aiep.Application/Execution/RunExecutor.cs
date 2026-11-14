using Aiep.Domain.Runs;

namespace Aiep.Application.Execution;

/// <summary>
/// Executes one claimed run end to end: resolve the allowlisted handler, run it under a hard timeout while heartbeating
/// the lease, then settle the outcome using the Domain's retry classification and backoff. Pure orchestration over the
/// ports (no host or database dependency), so it can be driven by the worker loop and tested directly.
/// </summary>
public sealed class RunExecutor(IRunQueue queue, IHandlerRegistry handlers, TimeProvider time)
{
    /// <summary>Executes and settles the run. Returns the terminal/retry status it settled, or null if it abandoned the
    /// run (lease lost or the worker is shutting down) and left recovery to the reconciler.</summary>
    public async Task<TaskRunStatus?> ExecuteAsync(ClaimedRun run, CancellationToken stoppingToken)
    {
        var handler = handlers.Resolve(run.HandlerType, run.HandlerVersion);
        if (handler is null)
        {
            // Not allowlisted (ADR-0007): a permanent failure, never retried.
            await queue.SettleAsync(Permanent(run, ErrorCategory.InvalidRequest,
                $"No handler '{run.HandlerType}' version '{run.HandlerVersion}'."), stoppingToken);
            return TaskRunStatus.Failed;
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        execution.CancelAfter(TimeSpan.FromSeconds(run.TimeoutSeconds));

        var cancelRequested = false;
        var leaseLost = false;
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync();

        try
        {
            var input = new HandlerInput(
                run.HandlerType, run.HandlerVersion, run.ModelProvider, run.ModelName, run.ModelConfig,
                run.PromptTemplate, run.InputJson, run.OutputSchema);

            var result = await handler.ExecuteAsync(input, execution.Token);
            await StopHeartbeatAsync();
            await queue.SettleAsync(Success(run, result), stoppingToken);
            return TaskRunStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancelRequested)
        {
            await StopHeartbeatAsync();
            await queue.SettleAsync(Cancelled(run), stoppingToken);
            return TaskRunStatus.Cancelled;
        }
        catch (OperationCanceledException) when (leaseLost || stoppingToken.IsCancellationRequested)
        {
            // The reconciler owns recovery now, or we are shutting down: leave the run as it is.
            await StopHeartbeatAsync();
            return null;
        }
        catch (OperationCanceledException)
        {
            // Hard timeout from the task-definition version.
            await StopHeartbeatAsync();
            return await SettleFailure(run, ErrorCategory.Timeout, "execution timed out", providerMayHaveCompleted: true, stoppingToken);
        }
        catch (ModelProviderException ex)
        {
            await StopHeartbeatAsync();
            return await SettleFailure(run, ex.Category, ex.Message, providerMayHaveCompleted: false, stoppingToken);
        }
        catch (HandlerException ex)
        {
            await StopHeartbeatAsync();
            return await SettleFailure(run, ex.Category, ex.Message, providerMayHaveCompleted: false, stoppingToken);
        }
        catch (Exception)
        {
            // An unexpected handler fault is treated as transient so a genuine glitch can recover, bounded by max_attempts.
            await StopHeartbeatAsync();
            return await SettleFailure(run, ErrorCategory.Transient, "unexpected handler error", providerMayHaveCompleted: false, stoppingToken);
        }

        async Task HeartbeatAsync()
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, run.LeaseSeconds / 3.0));
            try
            {
                using var timer = new PeriodicTimer(interval, time);
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                {
                    var beat = await queue.HeartbeatAsync(run, heartbeatStop.Token);
                    if (!beat.LeaseHeld)
                    {
                        leaseLost = true;
                        await execution.CancelAsync();
                        return;
                    }

                    if (beat.CancelRequested)
                    {
                        cancelRequested = true;
                        await execution.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Heartbeat stopped normally when the attempt finished.
            }
        }

        async Task StopHeartbeatAsync()
        {
            await heartbeatStop.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
    }

    private async Task<TaskRunStatus> SettleFailure(
        ClaimedRun run, ErrorCategory category, string message, bool providerMayHaveCompleted, CancellationToken ct)
    {
        if (!ErrorCategories.IsRetryable(category))
        {
            await queue.SettleAsync(Permanent(run, category, message, providerMayHaveCompleted), ct);
            return TaskRunStatus.Failed;
        }

        var policy = new RetryPolicy(RetryPolicy.Default.BaseDelay, RetryPolicy.Default.MaxDelay, run.MaxAttempts);
        if (!policy.HasAttemptsLeft(run.AttemptNumber))
        {
            await queue.SettleAsync(new RunSettlement(
                run.OrganizationId, run.WorkspaceId, run.RunId, run.AttemptId, run.WorkerId,
                TaskRunStatus.DeadLettered, "failed", ErrorCategory: category, ErrorMessage: message,
                ProviderCallMayHaveCompleted: providerMayHaveCompleted), ct);
            return TaskRunStatus.DeadLettered;
        }

        var nextAttemptAt = time.GetUtcNow() + RetryBackoff.Delay(run.AttemptNumber, policy);
        await queue.SettleAsync(new RunSettlement(
            run.OrganizationId, run.WorkspaceId, run.RunId, run.AttemptId, run.WorkerId,
            TaskRunStatus.RetryWait, "failed", ErrorCategory: category, ErrorMessage: message,
            NextAttemptAt: nextAttemptAt, ProviderCallMayHaveCompleted: providerMayHaveCompleted), ct);
        return TaskRunStatus.RetryWait;
    }

    private static RunSettlement Success(ClaimedRun run, HandlerResult result) => new(
        run.OrganizationId, run.WorkspaceId, run.RunId, run.AttemptId, run.WorkerId,
        TaskRunStatus.Succeeded, "succeeded", OutputJson: result.OutputJson, Usage: result.Usage);

    private static RunSettlement Cancelled(ClaimedRun run) => new(
        run.OrganizationId, run.WorkspaceId, run.RunId, run.AttemptId, run.WorkerId,
        TaskRunStatus.Cancelled, "cancelled", ProviderCallMayHaveCompleted: true);

    private static RunSettlement Permanent(
        ClaimedRun run, ErrorCategory category, string message, bool providerMayHaveCompleted = false) => new(
        run.OrganizationId, run.WorkspaceId, run.RunId, run.AttemptId, run.WorkerId,
        TaskRunStatus.Failed, "failed", ErrorCategory: category, ErrorMessage: message,
        ProviderCallMayHaveCompleted: providerMayHaveCompleted);
}
