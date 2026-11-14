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
}
