namespace Aiep.Application.Authorization;

/// <summary>
/// The role → permission table (ADR-0010). This is the single code copy of the matrix in
/// <c>docs/security/authorization-model.md</c>; <c>RolePermissionsMatrixTests</c> parses that doc and fails if
/// the two drift. A cell that the matrix marks with a footnote (for example <c>limited</c>, <c>own</c>,
/// <c>ws-scoped</c>) still grants the permission here; the footnote's condition is a step-6 policy enforced at the
/// endpoint (ownership, workspace scoping, owner-protection), not an absence of the permission.
/// </summary>
public static class RolePermissions
{
    // Owner holds every permission. Admin holds all but ownership transfer. Both are implicitly WorkspaceAdmin on every
    // workspace (ADR-0010), which is why their sets already include the workspace permissions.
    private static readonly IReadOnlySet<string> OwnerSet = Freeze(Permission.All);

    private static readonly IReadOnlySet<string> AdminSet = Freeze(Permission.All.Where(p => p != Permission.OrgTransfer));

    private static readonly IReadOnlySet<string> AuditorSet = Freeze(
    [
        Permission.OrgRead,
        Permission.MembersRead,
        Permission.DefinitionsRead,
        Permission.RunsRead,
        Permission.EvalsRead,
        Permission.KeysRead,
        Permission.RetentionRead,
        Permission.AuditRead,
        Permission.UsageRead,
    ]);

    // A plain Member has no permissions: organization membership alone grants no access (FR-005). Access arrives only
    // through a workspace grant.
    private static readonly IReadOnlySet<string> MemberSet = Freeze([]);

    private static readonly IReadOnlySet<string> WorkspaceAdminSet = Freeze(
    [
        Permission.WorkspacesManage,
        Permission.GrantsManage,
        Permission.DefinitionsRead,
        Permission.DefinitionsWrite,
        Permission.RunsSubmit,
        Permission.RunsRead,
        Permission.RunsReadResult,
        Permission.RunsCancel,
        Permission.RunsReplay,
        Permission.EvalsRead,
        Permission.EvalsWrite,
        Permission.KeysRead,
        Permission.KeysCreate,
        Permission.KeysRevoke,
        Permission.AuditRead,
        Permission.UsageRead,
    ]);

    private static readonly IReadOnlySet<string> DeveloperSet = Freeze(
    [
        Permission.DefinitionsRead,
        Permission.DefinitionsWrite,
        Permission.RunsSubmit,
        Permission.RunsRead,
        Permission.RunsReadResult,
        Permission.RunsCancel,
        Permission.RunsReplay,
        Permission.EvalsRead,
        Permission.KeysRead,
        Permission.KeysCreate,
        Permission.KeysRevoke,
        Permission.UsageRead,
    ]);

    private static readonly IReadOnlySet<string> EvaluatorSet = Freeze(
    [
        Permission.DefinitionsRead,
        Permission.RunsSubmit,
        Permission.RunsRead,
        Permission.RunsReadResult,
        Permission.RunsCancel,
        Permission.EvalsRead,
        Permission.EvalsWrite,
        Permission.UsageRead,
    ]);

    private static readonly IReadOnlySet<string> ViewerSet = Freeze(
    [
        Permission.DefinitionsRead,
        Permission.RunsRead,
        Permission.RunsReadResult,
        Permission.EvalsRead,
    ]);

    public static IReadOnlySet<string> ForOrgRole(OrgRole role) => role switch
    {
        OrgRole.Owner => OwnerSet,
        OrgRole.Admin => AdminSet,
        OrgRole.Auditor => AuditorSet,
        OrgRole.Member => MemberSet,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static IReadOnlySet<string> ForWorkspaceRole(WorkspaceRole role) => role switch
    {
        WorkspaceRole.WorkspaceAdmin => WorkspaceAdminSet,
        WorkspaceRole.Developer => DeveloperSet,
        WorkspaceRole.Evaluator => EvaluatorSet,
        WorkspaceRole.Viewer => ViewerSet,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static IReadOnlySet<string> Freeze(IEnumerable<string> permissions)
    {
        var set = new HashSet<string>(permissions, StringComparer.Ordinal);
        var unknown = set.FirstOrDefault(p => !Permission.IsKnown(p));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Role table references unknown permission '{unknown}'.");
        }

        return set;
    }
}
