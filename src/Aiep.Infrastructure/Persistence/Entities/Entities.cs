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
