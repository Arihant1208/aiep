using Aiep.Application.ControlPlane;

namespace Aiep.Application.Evaluation;

public sealed record EvaluationSuiteView(
    Guid Id, Guid WorkspaceId, string Slug, string Name, string? Description, string Status, int? CurrentVersion,
    DateTimeOffset CreatedAt);

public sealed record EvaluationSuiteVersionView(
    Guid Id, Guid SuiteId, int Version, string Status, int ExampleCount, DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt);

/// <summary>An evaluation run and, once scored, its summary JSON (counts, exact-match rate, latency and cost aggregates).</summary>
public sealed record EvaluationRunView(
    Guid Id, Guid SuiteVersionId, Guid TaskDefinitionVersionId, string Status, int ExampleCount, string? Summary,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>
/// Evaluation datasets and runs over the tenant unit of work (ADR-0014). Creating a run fans out one task_run per
/// example; scoring runs the versioned scorers over the completed task_runs and writes the per-example results and the
/// run summary.
/// </summary>
public interface IEvaluationStore
{
    Task<EvaluationSuiteView?> CreateSuiteAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string slug, string name, string? description,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvaluationSuiteView>> ListSuitesAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, CancellationToken cancellationToken = default);

    Task<EvaluationSuiteView?> GetSuiteAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid suiteId, CancellationToken cancellationToken = default);

    /// <summary>Creates the next draft version from a JSON array of {input, expected} examples; null if the suite is missing.</summary>
    Task<EvaluationSuiteVersionView?> CreateSuiteVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid suiteId, string examplesJson,
        CancellationToken cancellationToken = default);

    Task<EvaluationSuiteVersionView?> PublishSuiteVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid suiteId, int version,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an evaluation run: enqueues one task_run per example against the task-definition version. Null if the
    /// suite version or the definition version is missing or unpublished.</summary>
    Task<EvaluationRunView?> CreateEvaluationRunAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid suiteVersionId, Guid taskDefinitionVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>Scores the run if all its task_runs are terminal, writing per-example results and the summary; idempotent.
    /// Returns the current view (still Running if not all task_runs have finished).</summary>
    Task<EvaluationRunView?> ScoreAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid evaluationRunId, CancellationToken cancellationToken = default);

    Task<EvaluationRunView?> GetEvaluationRunAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid evaluationRunId, CancellationToken cancellationToken = default);
}
