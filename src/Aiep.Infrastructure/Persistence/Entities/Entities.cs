namespace Aiep.Infrastructure.Persistence.Entities;

// Persistence mappings for V0001 (db/migrations/V0001__tenancy_baseline.sql is the source of truth).
// Property names map to snake_case columns. Timestamps left at default(DateTimeOffset) use the database default.
// Roles and statuses stay strings here; Application owns their meaning (ADR-0010).

public sealed class Subject
{
    public Guid Id { get; set; }
    public required string Issuer { get; set; }
    public required string ExternalSubject { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Membership
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SubjectId { get; set; }
    public required string OrgRole { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Workspace
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkspaceMembership
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SubjectId { get; set; }
    public required string WorkspaceRole { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public required string KeyId { get; set; }
    public required byte[] SecretHash { get; set; }
    public required string Environment { get; set; }
    public required string Name { get; set; }
    public Guid OwnerSubjectId { get; set; }
    public required string[] Permissions { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string ActorType { get; set; }
    public Guid? ActorSubjectId { get; set; }
    public Guid? ActorApiKeyId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public required string Outcome { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
}

// --- V0002: task execution (db/migrations/V0002__task_execution.sql is the source of truth). -------------------
// jsonb columns are mapped as string in AiepDbContext; Application owns their JSON shape.

public sealed class TaskDefinition
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "active";
    public int? CurrentVersion { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TaskDefinitionVersion
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid TaskDefinitionId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = "draft";
    public required string InputSchema { get; set; }
    public required string OutputSchema { get; set; }
    public required string HandlerType { get; set; }
    public required string HandlerVersion { get; set; }
    public required string ModelProvider { get; set; }
    public required string ModelName { get; set; }
    public string ModelConfig { get; set; } = "{}";
    public string? PromptTemplate { get; set; }
    public int MaxAttempts { get; set; }
    public int TimeoutSeconds { get; set; }
    public int LeaseSeconds { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class TaskRun
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid TaskDefinitionVersionId { get; set; }
    public string Status { get; set; } = "Queued";
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public required string Input { get; set; }
    public string? Output { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public bool ProviderCallMayHaveCompleted { get; set; }
    public string? Traceparent { get; set; }
    public Guid? ReplayedFromRunId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TaskRunAttempt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RunId { get; set; }
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = "running";
    public required string WorkerId { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ProviderLatencyMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class IdempotencyKey
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CallerId { get; set; }
    public required byte[] KeyHash { get; set; }
    public required byte[] RequestHash { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
