using Aiep.Application.ControlPlane;
using Aiep.Application.Tenancy;

namespace Aiep.Application.Execution;

/// <summary>A run as seen by the API. <see cref="Output"/> is the result and is only returned to callers with the
/// result-read permission; the endpoint strips it otherwise.</summary>
public sealed record RunView(
    Guid Id,
    Guid WorkspaceId,
    Guid TaskDefinitionVersionId,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    string? Output,
    string? ErrorCategory,
    string? ErrorMessage,
    bool ProviderCallMayHaveCompleted,
    DateTimeOffset? CancelRequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

/// <summary>A run submission. Scope (organization) comes from the tenant context, never this request (CLAUDE.md).</summary>
public sealed record SubmitRunRequest(
    Guid WorkspaceId,
    Guid TaskDefinitionVersionId,
    string InputJson,
    int MaxAttempts,
    Guid CallerId,
    string? IdempotencyKey,
    string? Traceparent,
    Guid? CreatedBy);

public enum SubmitStatus
{
    Created,
    Existing,
    Conflict,
}

public sealed record SubmitResult(SubmitStatus Status, RunView? Run);

/// <summary>
/// Run submission and reads over the tenant unit of work. Submission is idempotent per (organization, workspace, caller,
/// key) and inserts the run atomically with its idempotency mapping (ADR-0015).
/// </summary>
public interface IRunStore
{
    Task<SubmitResult> SubmitAsync(TenantContext context, SubmitRunRequest request, TimeSpan idempotencyRetention,
        CancellationToken cancellationToken = default);

    Task<RunView?> GetRunAsync(Guid organizationId, Guid subjectId, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunView>> ListRunsAsync(Guid organizationId, Guid subjectId, Guid workspaceId, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation (ADR-0009, task-lifecycle.md): pending runs cancel immediately; a running run gets
    /// the flag and the worker stops at its next heartbeat. Null if the run does not exist in the workspace.</summary>
    Task<RunView?> CancelRunAsync(ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken = default);
}
