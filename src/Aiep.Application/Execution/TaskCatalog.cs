using Aiep.Application.ControlPlane;

namespace Aiep.Application.Execution;

/// <summary>A task definition (stable identity for a workspace task).</summary>
public sealed record TaskDefinitionView(
    Guid Id,
    Guid WorkspaceId,
    string Slug,
    string Name,
    string? Description,
    string Status,
    int? CurrentVersion,
    DateTimeOffset CreatedAt);

/// <summary>A task-definition version. Immutable once <see cref="PublishedAt"/> is set.</summary>
public sealed record TaskDefinitionVersionView(
    Guid Id,
    Guid DefinitionId,
    int Version,
    string Status,
    string InputSchema,
    string OutputSchema,
    string HandlerType,
    string HandlerVersion,
    string ModelProvider,
    string ModelName,
    string ModelConfig,
    string? PromptTemplate,
    int MaxAttempts,
    int TimeoutSeconds,
    int LeaseSeconds,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt);

/// <summary>The fields of a new draft version. Validation (JSON well-formedness, handler allowlist) happens at the edge.</summary>
public sealed record NewVersionSpec(
    string InputSchema,
    string OutputSchema,
    string HandlerType,
    string HandlerVersion,
    string ModelProvider,
    string ModelName,
    string ModelConfig,
    string? PromptTemplate,
    int MaxAttempts,
    int TimeoutSeconds,
    int LeaseSeconds);

/// <summary>
/// Task-definition and version authoring and reads, over the tenant unit of work. Every mutation writes its audit event
/// in the same transaction, as the control plane does.
/// </summary>
public interface ITaskCatalogStore
{
    Task<TaskDefinitionView?> CreateDefinitionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string slug, string name, string? description,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskDefinitionView>> ListDefinitionsAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, CancellationToken cancellationToken = default);

    Task<TaskDefinitionView?> GetDefinitionAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>Creates the next draft version of a definition, or null if the definition does not exist.</summary>
    Task<TaskDefinitionVersionView?> CreateVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid definitionId, NewVersionSpec spec,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes a draft version and points the definition's current_version at it; idempotent if already published.</summary>
    Task<TaskDefinitionVersionView?> PublishVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid definitionId, int version,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskDefinitionVersionView>> ListVersionsAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid definitionId, CancellationToken cancellationToken = default);

    Task<TaskDefinitionVersionView?> GetVersionAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid definitionId, int version, CancellationToken cancellationToken = default);
}
