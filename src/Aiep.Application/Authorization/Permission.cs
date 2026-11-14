namespace Aiep.Application.Authorization;

/// <summary>
/// The fine-grained permission strings (ADR-0010). Roles map to sets of these in <see cref="RolePermissions"/>, and an
/// API key carries a subset as its scope. The authoritative list is the matrix in
/// <c>docs/security/authorization-model.md</c>; <c>RolePermissionsMatrixTests</c> asserts code and doc agree.
/// </summary>
public static class Permission
{
    public const string OrgRead = "org:read";
    public const string OrgManage = "org:manage";
    public const string OrgTransfer = "org:transfer";

    public const string MembersRead = "members:read";
    public const string MembersManage = "members:manage";

    public const string WorkspacesCreate = "workspaces:create";
    public const string WorkspacesManage = "workspaces:manage";

    public const string GrantsManage = "grants:manage";

    public const string DefinitionsRead = "definitions:read";
    public const string DefinitionsWrite = "definitions:write";

    public const string RunsSubmit = "runs:submit";
    public const string RunsRead = "runs:read";
    public const string RunsReadResult = "runs:read_result";
    public const string RunsCancel = "runs:cancel";
    public const string RunsReplay = "runs:replay";

    public const string EvalsRead = "evals:read";
    public const string EvalsWrite = "evals:write";

    public const string KeysRead = "keys:read";
    public const string KeysCreate = "keys:create";
    public const string KeysRevoke = "keys:revoke";

    public const string RetentionRead = "retention:read";
    public const string RetentionManage = "retention:manage";

    public const string AuditRead = "audit:read";

    public const string UsageRead = "usage:read";

    /// <summary>Every permission the platform defines. Frozen; adding one is a matrix and ADR change.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        OrgRead, OrgManage, OrgTransfer,
        MembersRead, MembersManage,
        WorkspacesCreate, WorkspacesManage,
        GrantsManage,
        DefinitionsRead, DefinitionsWrite,
        RunsSubmit, RunsRead, RunsReadResult, RunsCancel, RunsReplay,
        EvalsRead, EvalsWrite,
        KeysRead, KeysCreate, KeysRevoke,
        RetentionRead, RetentionManage,
        AuditRead,
        UsageRead,
    };

    public static bool IsKnown(string permission) => All.Contains(permission);
}
