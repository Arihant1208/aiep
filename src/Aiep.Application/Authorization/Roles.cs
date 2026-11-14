namespace Aiep.Application.Authorization;

/// <summary>A subject's single organization role (ADR-0010). Stored as the enum name in <c>memberships.org_role</c>.</summary>
public enum OrgRole
{
    Owner,
    Admin,
    Auditor,
    Member,
}

/// <summary>A subject's role on one workspace (ADR-0010). Stored as the enum name in <c>workspace_memberships.workspace_role</c>.</summary>
public enum WorkspaceRole
{
    WorkspaceAdmin,
    Developer,
    Evaluator,
    Viewer,
}

/// <summary>
/// Strict parsing between the stored string values (the exact enum names, matching the V0001 CHECK constraints) and the
/// enums. Deliberately not <see cref="System.Enum.TryParse{T}(string, out T)"/>: that also accepts numbers and member
/// names in the wrong case, which a stored role must never be.
/// </summary>
public static class Roles
{
    public static bool TryParseOrgRole(string? value, out OrgRole role)
    {
        switch (value)
        {
            case "Owner": role = OrgRole.Owner; return true;
            case "Admin": role = OrgRole.Admin; return true;
            case "Auditor": role = OrgRole.Auditor; return true;
            case "Member": role = OrgRole.Member; return true;
            default: role = default; return false;
        }
    }

    public static OrgRole ParseOrgRole(string value) =>
        TryParseOrgRole(value, out var role)
            ? role
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown organization role.");

    public static bool TryParseWorkspaceRole(string? value, out WorkspaceRole role)
    {
        switch (value)
        {
            case "WorkspaceAdmin": role = WorkspaceRole.WorkspaceAdmin; return true;
            case "Developer": role = WorkspaceRole.Developer; return true;
            case "Evaluator": role = WorkspaceRole.Evaluator; return true;
            case "Viewer": role = WorkspaceRole.Viewer; return true;
            default: role = default; return false;
        }
    }

    public static WorkspaceRole ParseWorkspaceRole(string value) =>
        TryParseWorkspaceRole(value, out var role)
            ? role
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown workspace role.");

    /// <summary>The stored string for a role (its enum name).</summary>
    public static string ToDbValue(this OrgRole role) => role.ToString();

    public static string ToDbValue(this WorkspaceRole role) => role.ToString();
}
