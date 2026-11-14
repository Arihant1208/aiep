using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiep.Infrastructure.Execution;

/// <summary>
/// Task-definition and version authoring over <see cref="TenantDatabase"/> (ADR-0013), mirroring the control-plane
/// store: EF reads and writes, each mutation auditing in the same transaction. The version-immutability trigger (V0002)
/// enforces that a published version is never changed; publishing itself is allowed because published_at is still NULL.
/// </summary>
internal sealed class TaskCatalogStore(TenantDatabase database, TimeProvider time) : ITaskCatalogStore
{
    public Task<TaskDefinitionView?> CreateDefinitionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string slug, string name, string? description,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            if (await session.Db.TaskDefinitions.AsNoTracking()
                    .AnyAsync(d => d.WorkspaceId == workspaceId && d.Slug == slug, token))
            {
                return (TaskDefinitionView?)null;
            }

            var definition = new TaskDefinition
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                Slug = slug,
                Name = name,
                Description = description,
                CreatedBy = actor.SubjectId,
            };
            session.Db.TaskDefinitions.Add(definition);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "definition.created", definition.Id));
            await session.Db.SaveChangesAsync(token);
            return ToView(definition);
        }, cancellationToken);

    public Task<IReadOnlyList<TaskDefinitionView>> ListDefinitionsAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var definitions = await session.Db.TaskDefinitions.AsNoTracking()
                .Where(d => d.WorkspaceId == workspaceId)
                .OrderBy(d => d.Id)
                .ToListAsync(token);
            return (IReadOnlyList<TaskDefinitionView>)[.. definitions.Select(ToView)];
        }, cancellationToken);

    public Task<TaskDefinitionView?> GetDefinitionAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid definitionId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var definition = await session.Db.TaskDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(d => d.WorkspaceId == workspaceId && d.Id == definitionId, token);
            return definition is null ? null : ToView(definition);
        }, cancellationToken);

    public Task<TaskDefinitionVersionView?> CreateVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid definitionId, NewVersionSpec spec,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var definition = await session.Db.TaskDefinitions
                .SingleOrDefaultAsync(d => d.WorkspaceId == workspaceId && d.Id == definitionId, token);
            if (definition is null)
            {
                return (TaskDefinitionVersionView?)null;
            }

            var highest = await session.Db.TaskDefinitionVersions.AsNoTracking()
                .Where(v => v.TaskDefinitionId == definitionId)
                .MaxAsync(v => (int?)v.Version, token) ?? 0;

            var version = new TaskDefinitionVersion
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                TaskDefinitionId = definitionId,
                Version = highest + 1,
                Status = "draft",
                InputSchema = spec.InputSchema,
                OutputSchema = spec.OutputSchema,
                HandlerType = spec.HandlerType,
                HandlerVersion = spec.HandlerVersion,
                ModelProvider = spec.ModelProvider,
                ModelName = spec.ModelName,
                ModelConfig = spec.ModelConfig,
                PromptTemplate = spec.PromptTemplate,
                MaxAttempts = spec.MaxAttempts,
                TimeoutSeconds = spec.TimeoutSeconds,
                LeaseSeconds = spec.LeaseSeconds,
                CreatedBy = actor.SubjectId,
            };
            session.Db.TaskDefinitionVersions.Add(version);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "definition.version.created", version.Id));
            await session.Db.SaveChangesAsync(token);
            return ToView(version);
        }, cancellationToken);

    public Task<TaskDefinitionVersionView?> PublishVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid definitionId, int version,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var row = await session.Db.TaskDefinitionVersions
                .SingleOrDefaultAsync(v => v.TaskDefinitionId == definitionId && v.Version == version, token);
            if (row is null)
            {
                return (TaskDefinitionVersionView?)null;
            }

            if (row.Status != "published")
            {
                var now = time.GetUtcNow();
                row.Status = "published";
                row.PublishedAt = now;

                var definition = await session.Db.TaskDefinitions.SingleAsync(d => d.Id == definitionId, token);
                definition.CurrentVersion = version;
                definition.UpdatedAt = now;

                session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "definition.version.published", row.Id));
                await session.Db.SaveChangesAsync(token);
            }

            return ToView(row);
        }, cancellationToken);

    public Task<IReadOnlyList<TaskDefinitionVersionView>> ListVersionsAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid definitionId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var versions = await session.Db.TaskDefinitionVersions.AsNoTracking()
                .Where(v => v.WorkspaceId == workspaceId && v.TaskDefinitionId == definitionId)
                .OrderBy(v => v.Version)
                .ToListAsync(token);
            return (IReadOnlyList<TaskDefinitionVersionView>)[.. versions.Select(ToView)];
        }, cancellationToken);

    public Task<TaskDefinitionVersionView?> GetVersionAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid definitionId, int version, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var row = await session.Db.TaskDefinitionVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.WorkspaceId == workspaceId && v.TaskDefinitionId == definitionId && v.Version == version, token);
            return row is null ? null : ToView(row);
        }, cancellationToken);

    private static TaskDefinitionView ToView(TaskDefinition d) => new(
        d.Id, d.WorkspaceId, d.Slug, d.Name, d.Description, d.Status, d.CurrentVersion, d.CreatedAt);

    private static TaskDefinitionVersionView ToView(TaskDefinitionVersion v) => new(
        v.Id, v.TaskDefinitionId, v.Version, v.Status, v.InputSchema, v.OutputSchema, v.HandlerType, v.HandlerVersion,
        v.ModelProvider, v.ModelName, v.ModelConfig, v.PromptTemplate, v.MaxAttempts, v.TimeoutSeconds, v.LeaseSeconds,
        v.PublishedAt, v.CreatedAt);

    private static AuditEvent Audit(ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string action, Guid targetId) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        WorkspaceId = workspaceId,
        ActorType = actor.ActorType,
        ActorSubjectId = actor.SubjectId,
        ActorApiKeyId = actor.ApiKeyId,
        Action = action,
        TargetType = "task_definition",
        TargetId = targetId,
        Outcome = "success",
        CorrelationId = actor.CorrelationId,
    };
}
