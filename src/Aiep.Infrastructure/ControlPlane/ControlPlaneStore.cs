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
internal sealed partial class ControlPlaneStore(TenantDatabase database, TimeProvider time) : IControlPlaneStore
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

    public Task<WorkspaceView?> CreateWorkspaceAsync(
        ControlPlaneActor actor, Guid organizationId, string slug, string name, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            if (await session.Db.Workspaces.AsNoTracking().AnyAsync(w => w.Slug == slug, token))
            {
                return (WorkspaceView?)null;
            }

            var workspace = new Workspace
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                Slug = slug,
                Name = name,
                CreatedBy = actor.SubjectId,
            };
            session.Db.Workspaces.Add(workspace);
            session.Db.AuditEvents.Add(
                Audit(actor, organizationId, workspace.Id, "workspace.created", "workspace", workspace.Id));
            await session.Db.SaveChangesAsync(token);
            return ToView(workspace);
        }, ct);

    public Task<IReadOnlyList<WorkspaceView>> ListWorkspacesAsync(
        Guid organizationId, Guid subjectId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var workspaces = await session.Db.Workspaces.AsNoTracking()
                .OrderBy(w => w.Id)
                .ToListAsync(token);
            return (IReadOnlyList<WorkspaceView>)[.. workspaces.Select(ToView)];
        }, ct);

    public Task<WorkspaceView?> GetWorkspaceAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var workspace = await session.Db.Workspaces.AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == workspaceId, token);
            return workspace is null ? null : ToView(workspace);
        }, ct);

    public Task<WorkspaceView?> ArchiveWorkspaceAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var workspace = await session.Db.Workspaces.SingleOrDefaultAsync(w => w.Id == workspaceId, token);
            if (workspace is null)
            {
                return (WorkspaceView?)null;
            }

            // Idempotent: archiving an already-archived workspace changes nothing and writes no audit event.
            if (workspace.Status == "active")
            {
                var now = time.GetUtcNow();
                workspace.Status = "archived";
                workspace.ArchivedAt = now;
                workspace.UpdatedAt = now;
                session.Db.AuditEvents.Add(
                    Audit(actor, organizationId, workspace.Id, "workspace.archived", "workspace", workspace.Id));
                await session.Db.SaveChangesAsync(token);
            }

            return ToView(workspace);
        }, ct);

    public Task<WorkspaceGrantView?> SetWorkspaceGrantAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid subjectId, WorkspaceRole role, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            // The grant's composite FK requires a membership in this organization; check first for a clean 404.
            if (!await session.Db.Memberships.AsNoTracking().AnyAsync(m => m.SubjectId == subjectId, token))
            {
                return (WorkspaceGrantView?)null;
            }

            var roleName = role.ToDbValue();
            var grant = await session.Db.WorkspaceMemberships
                .SingleOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.SubjectId == subjectId, token);
            if (grant is null)
            {
                session.Db.WorkspaceMemberships.Add(new WorkspaceMembership
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    WorkspaceId = workspaceId,
                    SubjectId = subjectId,
                    WorkspaceRole = roleName,
                    CreatedBy = actor.SubjectId,
                });
            }
            else
            {
                grant.WorkspaceRole = roleName;
                grant.UpdatedAt = time.GetUtcNow();
            }

            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "grant.set", "subject", subjectId));
            await session.Db.SaveChangesAsync(token);
            return new WorkspaceGrantView(workspaceId, subjectId, roleName);
        }, ct);

    public Task<bool> RemoveWorkspaceGrantAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid subjectId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var grant = await session.Db.WorkspaceMemberships
                .SingleOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.SubjectId == subjectId, token);
            if (grant is null)
            {
                return false;
            }

            session.Db.WorkspaceMemberships.Remove(grant);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "grant.removed", "subject", subjectId));
            await session.Db.SaveChangesAsync(token);
            return true;
        }, ct);

    public Task<IReadOnlyList<MemberView>> ListMembersAsync(Guid organizationId, Guid subjectId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var members = await (
                from m in session.Db.Memberships.AsNoTracking()
                join s in session.Db.Subjects.AsNoTracking() on m.SubjectId equals s.Id
                orderby m.CreatedAt
                select new MemberView(m.SubjectId, m.OrgRole, m.Status, s.DisplayName, s.Email, m.CreatedAt))
                .ToListAsync(token);
            return (IReadOnlyList<MemberView>)members;
        }, ct);

    public Task<MemberChangeResult> AddMemberAsync(
        ControlPlaneActor actor, Guid organizationId, OrgRole actorRole, Guid subjectId, OrgRole role, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (TenantSession session, CancellationToken token) =>
        {
            if (role == OrgRole.Owner && actorRole != OrgRole.Owner)
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.OwnerProtected);
            }

            if (await session.Db.Memberships.AsNoTracking().AnyAsync(m => m.SubjectId == subjectId, token))
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.AlreadyMember);
            }

            // A not-yet-member subject is invisible under RLS, so existence can't be pre-checked; the FK is the only
            // guard. Insert inside a savepoint so a missing subject (23503) becomes a clean 404 without poisoning the
            // outer transaction.
            await session.Transaction.SaveAsync("add_member", token);
            try
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO aiep.memberships (id, organization_id, subject_id, org_role, created_by) VALUES ($1, $2, $3, $4, $5)",
                    session.Connection, session.Transaction);
                insert.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
                insert.Parameters.Add(new NpgsqlParameter { Value = organizationId });
                insert.Parameters.Add(new NpgsqlParameter { Value = subjectId });
                insert.Parameters.Add(new NpgsqlParameter { Value = role.ToDbValue() });
                insert.Parameters.Add(new NpgsqlParameter { Value = actor.SubjectId });
                await insert.ExecuteNonQueryAsync(token);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                await session.Transaction.RollbackAsync("add_member", token);
                return MemberChangeResult.Fail(MemberChangeOutcome.SubjectNotFound);
            }

            session.Db.AuditEvents.Add(Audit(actor, organizationId, null, "member.added", "subject", subjectId));
            await session.Db.SaveChangesAsync(token);
            return await LoadMemberAsync(session, subjectId, token);
        }, ct);

    public Task<MemberChangeResult> ChangeMemberRoleAsync(
        ControlPlaneActor actor, Guid organizationId, OrgRole actorRole, Guid subjectId, OrgRole role, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var membership = await session.Db.Memberships.SingleOrDefaultAsync(m => m.SubjectId == subjectId, token);
            if (membership is null || !Roles.TryParseOrgRole(membership.OrgRole, out var currentRole))
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.NotAMember);
            }

            // Only an Owner may change an Owner's role or promote someone to Owner.
            if ((currentRole == OrgRole.Owner || role == OrgRole.Owner) && actorRole != OrgRole.Owner)
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.OwnerProtected);
            }

            if (currentRole == OrgRole.Owner && role != OrgRole.Owner && await IsLastOwnerAsync(session, subjectId, token))
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.LastOwner);
            }

            if (currentRole != role)
            {
                membership.OrgRole = role.ToDbValue();
                membership.UpdatedAt = time.GetUtcNow();
                session.Db.AuditEvents.Add(Audit(actor, organizationId, null, "member.role_changed", "subject", subjectId));
                await session.Db.SaveChangesAsync(token);
            }

            return await LoadMemberAsync(session, subjectId, token);
        }, ct);

    public Task<MemberChangeResult> RemoveMemberAsync(
        ControlPlaneActor actor, Guid organizationId, OrgRole actorRole, Guid subjectId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var membership = await session.Db.Memberships.SingleOrDefaultAsync(m => m.SubjectId == subjectId, token);
            if (membership is null)
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.NotAMember);
            }

            var isOwner = Roles.TryParseOrgRole(membership.OrgRole, out var role) && role == OrgRole.Owner;
            if (isOwner && actorRole != OrgRole.Owner)
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.OwnerProtected);
            }

            if (isOwner && await IsLastOwnerAsync(session, subjectId, token))
            {
                return MemberChangeResult.Fail(MemberChangeOutcome.LastOwner);
            }

            session.Db.Memberships.Remove(membership); // workspace grants cascade (composite FK)
            session.Db.AuditEvents.Add(Audit(actor, organizationId, null, "member.removed", "subject", subjectId));
            await session.Db.SaveChangesAsync(token);
            return new MemberChangeResult(MemberChangeOutcome.Ok, null);
        }, ct);

    public Task<ApiKeyView> CreateApiKeyAsync(
        ControlPlaneActor actor, Guid organizationId, Guid? workspaceId, string keyId, byte[] secretHash,
        string environment, string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var key = new ApiKey
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                KeyId = keyId,
                SecretHash = secretHash,
                Environment = environment,
                Name = name,
                OwnerSubjectId = actor.SubjectId,
                Permissions = [.. permissions],
                ExpiresAt = expiresAt,
                CreatedBy = actor.SubjectId,
            };
            session.Db.ApiKeys.Add(key);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "api_key.created", "api_key", key.Id));
            await session.Db.SaveChangesAsync(token);
            return ToView(key);
        }, ct);

    public Task<IReadOnlyList<ApiKeyView>> ListApiKeysAsync(Guid organizationId, Guid subjectId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var keys = await session.Db.ApiKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(token);
            return (IReadOnlyList<ApiKeyView>)[.. keys.Select(ToView)];
        }, ct);

    public Task<ApiKeyRevokeOutcome> RevokeApiKeyAsync(
        ControlPlaneActor actor, Guid organizationId, Guid apiKeyId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var key = await session.Db.ApiKeys.SingleOrDefaultAsync(k => k.Id == apiKeyId, token);
            if (key is null)
            {
                return ApiKeyRevokeOutcome.NotFound;
            }

            if (key.RevokedAt is not null)
            {
                return ApiKeyRevokeOutcome.AlreadyRevoked;
            }

            key.RevokedAt = time.GetUtcNow();
            key.RevokedBy = actor.SubjectId;
            session.Db.AuditEvents.Add(Audit(actor, organizationId, key.WorkspaceId, "api_key.revoked", "api_key", key.Id));
            await session.Db.SaveChangesAsync(token);
            return ApiKeyRevokeOutcome.Revoked;
        }, ct);

    private static ApiKeyView ToView(ApiKey key) => new(
        key.Id, key.Name, key.Environment, key.WorkspaceId, key.OwnerSubjectId, key.Permissions,
        key.ExpiresAt, key.LastUsedAt, key.RevokedAt, key.CreatedAt);

    private static async Task<bool> IsLastOwnerAsync(TenantSession session, Guid subjectId, CancellationToken ct)
    {
        var owners = await session.Db.Memberships.AsNoTracking()
            .Where(m => m.OrgRole == "Owner" && m.Status == "active")
            .Select(m => m.SubjectId)
            .ToListAsync(ct);
        return AuthorizationInvariants.WouldRemoveLastOwner(owners, subjectId);
    }

    private static async Task<MemberChangeResult> LoadMemberAsync(TenantSession session, Guid subjectId, CancellationToken ct)
    {
        var member = await (
            from m in session.Db.Memberships.AsNoTracking()
            join s in session.Db.Subjects.AsNoTracking() on m.SubjectId equals s.Id
            where m.SubjectId == subjectId
            select new MemberView(m.SubjectId, m.OrgRole, m.Status, s.DisplayName, s.Email, m.CreatedAt))
            .SingleAsync(ct);
        return new MemberChangeResult(MemberChangeOutcome.Ok, member);
    }

    private static WorkspaceView ToView(Workspace workspace) =>
        new(workspace.Id, workspace.Slug, workspace.Name, workspace.Status, workspace.ArchivedAt, workspace.CreatedAt);

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
