using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;

namespace Aiep.Application.Execution;

/// <summary>A run to enqueue, in the same transaction as acceptance (ADR-0005). The run id is generated and returned.</summary>
public sealed record EnqueueRunRequest(
    Guid WorkspaceId,
    Guid TaskDefinitionVersionId,
    string InputJson,
    int MaxAttempts,
    string? Traceparent = null,
    Guid? CreatedBy = null,
    Guid? ReplayedFromRunId = null);

/// <summary>A run claimed by a worker: the lease is held, the attempt row exists, and all the config to execute it.</summary>
public sealed record ClaimedRun(
    Guid RunId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid AttemptId,
    int AttemptNumber,
    int MaxAttempts,
    string WorkerId,
    string InputJson,
    string HandlerType,
    string HandlerVersion,
    string ModelProvider,
    string ModelName,
    string ModelConfig,
    string? PromptTemplate,
    string OutputSchema,
    int TimeoutSeconds,
    int LeaseSeconds,
    DateTimeOffset LeaseExpiresAt,
    bool CancelRequested);

/// <summary>The result of a heartbeat: whether the lease is still ours, and whether cancellation was requested.</summary>
public sealed record HeartbeatResult(bool LeaseHeld, bool CancelRequested);

/// <summary>
/// How an attempt finished. The worker computes the target state with the Domain state machine and backoff, then the
/// queue applies it as a conditional update so a late or duplicate attempt that lost the lease cannot overwrite the
/// result (at most one terminal outcome per run, task-lifecycle.md).
/// </summary>
public sealed record RunSettlement(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid RunId,
    Guid AttemptId,
    string WorkerId,
    TaskRunStatus TargetStatus,
    string AttemptStatus,
    string? OutputJson = null,
    ErrorCategory? ErrorCategory = null,
    string? ErrorMessage = null,
    DateTimeOffset? NextAttemptAt = null,
    ModelUsage? Usage = null,
    bool ProviderCallMayHaveCompleted = false);

/// <summary>
/// The PostgreSQL-backed work queue (ADR-0005), behind a port so a broker could replace it without touching
/// orchestration. Claim and reconcile are cross-tenant; enqueue, heartbeat and settle run under the tenant context of
/// the run's organization.
/// </summary>
public interface IRunQueue
{
    Task<Guid> EnqueueAsync(TenantContext context, EnqueueRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Claim the next eligible run across all tenants, or <c>null</c> if none is ready.</summary>
    Task<ClaimedRun?> ClaimAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>Extend the lease; <see cref="HeartbeatResult.LeaseHeld"/> is false if the lease was lost or the run moved on.</summary>
    Task<HeartbeatResult> HeartbeatAsync(ClaimedRun run, CancellationToken cancellationToken = default);

    Task SettleAsync(RunSettlement settlement, CancellationToken cancellationToken = default);

    /// <summary>Recover runs whose worker lease expired; returns how many were recovered.</summary>
    Task<int> ReconcileExpiredLeasesAsync(int maxRuns = 100, CancellationToken cancellationToken = default);

    /// <summary>Wait for an enqueue signal (LISTEN/NOTIFY), returning true if one arrived before the timeout.</summary>
    Task<bool> WaitForEnqueueSignalAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
