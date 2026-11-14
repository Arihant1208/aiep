using System.Text.RegularExpressions;
using Aiep.Application.Authorization;

namespace Aiep.Application.Tests.Authorization;

/// <summary>
/// The authorization model doc is the spec (ADR-0010). These tests parse its role→permission matrix and fail if the
/// code table in <see cref="RolePermissions"/> drifts from it in either direction, so the doc and the enforced rules
/// can never silently disagree.
/// </summary>
public sealed partial class RolePermissionsMatrixTests
{
    // Column order in the matrix header: | Permission | O | A | Au | WA | D | E | V |
    private static readonly OrgRole?[] OrgColumns = [OrgRole.Owner, OrgRole.Admin, OrgRole.Auditor, null, null, null, null];

    private static readonly WorkspaceRole?[] WorkspaceColumns =
        [null, null, null, WorkspaceRole.WorkspaceAdmin, WorkspaceRole.Developer, WorkspaceRole.Evaluator, WorkspaceRole.Viewer];

    [Fact]
    public void Code_table_matches_the_documented_matrix()
    {
        var rows = ParseMatrix();

        // Build what the doc says each role should have, then compare set-for-set with the code.
        foreach (var (role, _) in OrgColumns.Select((r, i) => (r, i)).Where(x => x.r is not null))
        {
            var orgRole = role!.Value;
            var expected = ExpectedFor(rows, OrgColumns, orgRole);
            Assert.Equal(expected, new SortedSet<string>(RolePermissions.ForOrgRole(orgRole), StringComparer.Ordinal));
        }

        foreach (var role in WorkspaceColumns.Where(r => r is not null))
        {
            var wsRole = role!.Value;
            var expected = ExpectedForWorkspace(rows, wsRole);
            Assert.Equal(expected, new SortedSet<string>(RolePermissions.ForWorkspaceRole(wsRole), StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Matrix_permissions_are_exactly_the_code_permissions()
    {
        var documented = new SortedSet<string>(ParseMatrix().Select(r => r.Permission), StringComparer.Ordinal);
        var code = new SortedSet<string>(Permission.All, StringComparer.Ordinal);

        Assert.Equal(code, documented);
    }

    [Fact]
    public void Member_has_no_permissions()
    {
        // The matrix has no Member column: organization membership alone grants nothing (FR-005).
        Assert.Empty(RolePermissions.ForOrgRole(OrgRole.Member));
    }

    private static SortedSet<string> ExpectedFor(IReadOnlyList<MatrixRow> rows, OrgRole?[] columns, OrgRole role)
    {
        var column = Array.IndexOf(columns, role);
        return new SortedSet<string>(rows.Where(r => r.Granted[column]).Select(r => r.Permission), StringComparer.Ordinal);
    }

    private static SortedSet<string> ExpectedForWorkspace(IReadOnlyList<MatrixRow> rows, WorkspaceRole role)
    {
        var column = Array.IndexOf(WorkspaceColumns, role);
        return new SortedSet<string>(rows.Where(r => r.Granted[column]).Select(r => r.Permission), StringComparer.Ordinal);
    }

    private static IReadOnlyList<MatrixRow> ParseMatrix()
    {
        var lines = File.ReadAllLines(FindDoc());
        var start = Array.FindIndex(lines, l => l.StartsWith("## Role", StringComparison.Ordinal));
        Assert.True(start >= 0, "Could not find the matrix heading in the authorization model doc.");
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0)
        {
            end = lines.Length;
        }

        var rows = new List<MatrixRow>();
        var token = PermissionToken();
        for (var i = start; i < end; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            // Leading and trailing pipes produce empty first/last cells: content is cells[1..^1].
            var content = cells[1..^1];
            if (content.Length != 8)
            {
                continue; // header separator or a malformed row
            }

            var match = token.Match(content[0]);
            if (!match.Success)
            {
                continue; // the "Read secret values" row and any prose row
            }

            var granted = new bool[7];
            for (var c = 0; c < 7; c++)
            {
                var cell = content[c + 1];
                granted[c] = cell.Length > 0 && cell != "✗";
            }

            rows.Add(new MatrixRow(match.Value, granted));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static string FindDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "security", "authorization-model.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate docs/security/authorization-model.md from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("[a-z]+:[a-z_]+")]
    private static partial Regex PermissionToken();

    private sealed record MatrixRow(string Permission, bool[] Granted);
}
