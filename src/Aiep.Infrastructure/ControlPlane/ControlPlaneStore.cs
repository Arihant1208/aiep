using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aiep.Infrastructure.ControlPlane;

/// <summary>
/// Control-plane persistence over <see cref="TenantDatabase"/> (ADR-0006, ADR-0013). Reads use EF; the organization
/// bootstrap uses the <c>create_organization</c> SECURITY DEFINER function (ADR-0017). Each mutating method writes its
/// audit event in the same transaction as the change.
/// </summary>
internal sealed partial class ControlPlaneStore(TenantDatabase database) : IControlPlaneStore
{
    public Task<AuthorizationSubject> ResolveAuthorizationAsync(
        Guid organizationId, Guid subjectId, IReadOnlyList<string>? keyScope, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var membership = await session.Db.Memberships.AsNoTracking()
                .SingleOrDefaultAsync(m => m.SubjectId == subjectId, token);

            if (membership is null || membership.Status != "active"
                || !Roles.TryParseOrgRole(membership.OrgRole, out var orgRole))
            {
                return new AuthorizationSubject
                {
                    SubjectId = subjectId,
                    OrganizationId = organizationId,
                    IsActiveMember = false,
                    KeyScope = keyScope,
                };
            }

            var grants = await session.Db.WorkspaceMemberships.AsNoTracking()
                .Where(w => w.SubjectId == subjectId)
                .ToListAsync(token);

            var map = new Dictionary<Guid, WorkspaceRole>();
            foreach (var grant in grants)
            {
                if (Roles.TryParseWorkspaceRole(grant.WorkspaceRole, out var role))
                {
                    map[grant.WorkspaceId] = role;
                }
            }

            return new AuthorizationSubject
            {
                SubjectId = subjectId,
                OrganizationId = organizationId,
                IsActiveMember = true,
                OrgRole = orgRole,
                WorkspaceGrants = map,
                KeyScope = keyScope,
            };
        }, ct);

    public async Task<OrganizationView> CreateOrganizationAsync(ControlPlaneActor actor, string name, CancellationToken ct)
    {
        var organizationId = Guid.CreateVersion7();
        var ownerMembershipId = Guid.CreateVersion7();

        // No tenant context yet: the function creates the org and the Owner membership for the subject in app.subject_id.
        await database.ExecuteAsync(TenantContext.ForSubject(actor.SubjectId), async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT aiep.create_organization($1, $2, $3)", session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = organizationId });
            command.Parameters.Add(new NpgsqlParameter { Value = name });
            command.Parameters.Add(new NpgsqlParameter { Value = ownerMembershipId });
            await command.ExecuteNonQueryAsync(token);
        }, ct);

        // Now in the new organization's context: read it back and record the audit event in one transaction.
        return await database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var organization = await session.Db.Organizations.AsNoTracking().SingleAsync(o => o.Id == organizationId, token);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, null, "organization.created", "organization", organizationId));
            return new OrganizationView(organization.Id, organization.Name, organization.Status, organization.CreatedAt);
        }, ct);
    }

    public Task<OrganizationView?> GetOrganizationAsync(Guid organizationId, Guid subjectId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var organization = await session.Db.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(o => o.Id == organizationId, token);
            return organization is null
                ? null
                : new OrganizationView(organization.Id, organization.Name, organization.Status, organization.CreatedAt);
        }, ct);

    // Builds an audit row for the current transaction. occurred_at takes the column default (now()).
    private static AuditEvent Audit(
        ControlPlaneActor actor, Guid organizationId, Guid? workspaceId, string action, string? targetType,
        Guid? targetId, string outcome = "success", string? reason = null) => new()
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            ActorType = actor.ActorType,
            ActorSubjectId = actor.SubjectId,
            ActorApiKeyId = actor.ApiKeyId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Outcome = outcome,
            Reason = reason,
            CorrelationId = actor.CorrelationId,
        };
}
