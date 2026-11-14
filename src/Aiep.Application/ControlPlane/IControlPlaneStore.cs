using Aiep.Application.Authorization;

namespace Aiep.Application.ControlPlane;

/// <summary>
/// Persistence for the control plane (organizations, members, workspaces, grants, API keys). Every method runs inside
/// the tenant-context unit of work (ADR-0006); mutating methods write their audit event in the same transaction, so a
/// change and its audit record commit or roll back together. Authorization is the caller's responsibility (the
/// endpoints resolve a subject here, then check <see cref="IAuthorizationService"/> before mutating).
/// </summary>
public interface IControlPlaneStore
{
    /// <summary>
    /// Load the caller's current roles in one organization for an authorization decision (algorithm step 3): their
    /// active membership and workspace grants. Returns a subject with <c>IsActiveMember = false</c> when there is no
    /// active membership — the endpoint turns that into a 404, concealing the organization (ADR-0015).
    /// </summary>
    Task<AuthorizationSubject> ResolveAuthorizationAsync(
        Guid organizationId, Guid subjectId, IReadOnlyList<string>? keyScope, CancellationToken ct);

    /// <summary>
    /// Create an organization and its first Owner membership atomically (the <c>create_organization</c> bootstrap
    /// function, ADR-0017), then record the audit event. The actor becomes the Owner.
    /// </summary>
    Task<OrganizationView> CreateOrganizationAsync(ControlPlaneActor actor, string name, CancellationToken ct);

    /// <summary>Read one organization in its own context. Null if it is not visible (should not happen after a membership check).</summary>
    Task<OrganizationView?> GetOrganizationAsync(Guid organizationId, Guid subjectId, CancellationToken ct);

    /// <summary>Create a workspace and audit it. Null if the slug is already taken in the organization (409).</summary>
    Task<WorkspaceView?> CreateWorkspaceAsync(
        ControlPlaneActor actor, Guid organizationId, string slug, string name, CancellationToken ct);

    /// <summary>All workspaces in the organization (the endpoint filters to the ones the caller can see).</summary>
    Task<IReadOnlyList<WorkspaceView>> ListWorkspacesAsync(Guid organizationId, Guid subjectId, CancellationToken ct);

    /// <summary>One workspace by id, or null if it does not exist in the organization.</summary>
    Task<WorkspaceView?> GetWorkspaceAsync(Guid organizationId, Guid subjectId, Guid workspaceId, CancellationToken ct);

    /// <summary>Archive a workspace (idempotent) and audit the transition. Null if it does not exist.</summary>
    Task<WorkspaceView?> ArchiveWorkspaceAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, CancellationToken ct);

    /// <summary>
    /// Grant (or change) a subject's workspace role and audit it. Null if the subject has no membership in the
    /// organization (the composite FK would reject the grant; the endpoint returns 404).
    /// </summary>
    Task<WorkspaceGrantView?> SetWorkspaceGrantAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid subjectId, WorkspaceRole role, CancellationToken ct);

    /// <summary>Remove a subject's workspace grant and audit it. False if there was no grant (404).</summary>
    Task<bool> RemoveWorkspaceGrantAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid subjectId, CancellationToken ct);

    /// <summary>The organization's members, with their display data (visible to co-members).</summary>
    Task<IReadOnlyList<MemberView>> ListMembersAsync(Guid organizationId, Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Add an existing subject as a member with an organization role, and audit it. <paramref name="actorRole"/> gates
    /// the Owner-protection rule (only an Owner may add an Owner).
    /// </summary>
    Task<MemberChangeResult> AddMemberAsync(
        ControlPlaneActor actor, Guid organizationId, OrgRole actorRole, Guid subjectId, OrgRole role, CancellationToken ct);

    /// <summary>Change a member's organization role, enforcing Owner-protection and the last-Owner invariant, and audit it.</summary>
    Task<MemberChangeResult> ChangeMemberRoleAsync(
        ControlPlaneActor actor, Guid organizationId, OrgRole actorRole, Guid subjectId, OrgRole role, CancellationToken ct);

    /// <summary>Remove a member (workspace grants cascade), enforcing Owner-protection and the last-Owner invariant, and audit it.</summary>
    Task<MemberChangeResult> RemoveMemberAsync(
        ControlPlaneActor actor, Guid organizationId, OrgRole actorRole, Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Store a new API key (its secret already hashed by the caller) owned by the actor, and audit it. The endpoint has
    /// already checked that the permissions are within the creator's own (ADR-0008).
    /// </summary>
    Task<ApiKeyView> CreateApiKeyAsync(
        ControlPlaneActor actor, Guid organizationId, Guid? workspaceId, string keyId, byte[] secretHash,
        string environment, string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, CancellationToken ct);

    /// <summary>The organization's API keys, metadata only (never the secret).</summary>
    Task<IReadOnlyList<ApiKeyView>> ListApiKeysAsync(Guid organizationId, Guid subjectId, CancellationToken ct);

    /// <summary>Revoke an API key (soft; sets revoked_at) and audit it. Idempotent.</summary>
    Task<ApiKeyRevokeOutcome> RevokeApiKeyAsync(
        ControlPlaneActor actor, Guid organizationId, Guid apiKeyId, CancellationToken ct);
}
