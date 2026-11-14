namespace Aiep.Application.Authorization;

/// <summary>
/// A caller resolved for authorization in one organization: the result of algorithm steps 1–3 (authenticate, resolve
/// scope, confirm membership) in <c>docs/security/authorization-model.md</c>. Infrastructure builds it from the current
/// <c>memberships</c> and <c>workspace_memberships</c> rows (step 6 wires that up); this type holds no identity claims
/// and performs step 4, the effective-permission computation.
/// </summary>
public sealed record AuthorizationSubject
{
    private static readonly IReadOnlyDictionary<Guid, WorkspaceRole> NoGrants =
        new Dictionary<Guid, WorkspaceRole>();

    public required Guid SubjectId { get; init; }

    public required Guid OrganizationId { get; init; }

    /// <summary>True only while the subject has an <c>active</c> membership in <see cref="OrganizationId"/>.</summary>
    public required bool IsActiveMember { get; init; }

    /// <summary>The organization role, or null when the subject is not a member.</summary>
    public OrgRole? OrgRole { get; init; }

    /// <summary>Explicit workspace-role grants, by workspace id. Empty for a member with no grants.</summary>
    public IReadOnlyDictionary<Guid, WorkspaceRole> WorkspaceGrants { get; init; } = NoGrants;

    /// <summary>
    /// For an API key caller, the key's permission scope — a ceiling the effective set is intersected with (ADR-0008).
    /// Null for a human caller (no ceiling).
    /// </summary>
    public IReadOnlyList<string>? KeyScope { get; init; }

    /// <summary>Effective permissions for organization-level actions (no workspace in scope).</summary>
    public IReadOnlySet<string> EffectiveAtOrganization() => Cap(OrgWide());

    /// <summary>Effective permissions for actions scoped to one workspace.</summary>
    public IReadOnlySet<string> EffectiveInWorkspace(Guid workspaceId)
    {
        var effective = OrgWide();
        if (IsActiveMember && OrgRole is { } role)
        {
            // Owner and Admin are implicitly WorkspaceAdmin on every workspace (ADR-0010). Their org-role set already
            // folds those permissions in; unioning again keeps the rule visible and correct if the matrix changes.
            if (role is Authorization.OrgRole.Owner or Authorization.OrgRole.Admin)
            {
                effective.UnionWith(RolePermissions.ForWorkspaceRole(WorkspaceRole.WorkspaceAdmin));
            }

            if (WorkspaceGrants.TryGetValue(workspaceId, out var workspaceRole))
            {
                effective.UnionWith(RolePermissions.ForWorkspaceRole(workspaceRole));
            }
        }

        return Cap(effective);
    }

    private HashSet<string> OrgWide()
    {
        var effective = new HashSet<string>(StringComparer.Ordinal);
        if (IsActiveMember && OrgRole is { } role)
        {
            effective.UnionWith(RolePermissions.ForOrgRole(role));
        }

        return effective;
    }

    // An API key can never exceed its scope, and the scope is re-intersected with the owner's current permissions on
    // every request: when the owner loses a role or membership, OrgWide() shrinks and the key's power shrinks with it.
    private IReadOnlySet<string> Cap(HashSet<string> effective)
    {
        if (KeyScope is { } scope)
        {
            effective.IntersectWith(scope);
        }

        return effective;
    }
}
