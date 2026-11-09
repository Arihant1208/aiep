using Npgsql;

namespace Aiep.Database.Tests;

/// <summary>
/// Database-level isolation (ADR-0006 layers 2 and 3). Catalog-driven where possible, so a new tenant-owned table
/// that forgets RLS, FORCE, the policy or test data fails here without anyone remembering to add a test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests(PostgresFixture db)
{
    private readonly TenantDb _tdb = new(db);

    /// <summary>Tables that are deliberately not tenant-owned, with the reason documented in V0001.</summary>
    private static readonly HashSet<string> GlobalTables = ["schema_migrations", "subjects"];

    private async Task<List<string>> TenantTablesAsync()
    {
        var tables = await _tdb.RunAsync("aiep_migrator", null, null, c => TenantDb.ColumnAsync<string>(c,
            "SELECT c.relname::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'aiep' AND c.relkind IN ('r', 'p') ORDER BY 1"));
        return [.. tables.Where(name => !GlobalTables.Contains(name))];
    }

    private static string OrgColumn(string table) => table == "organizations" ? "id" : "organization_id";

    [Fact]
    public async Task Every_tenant_table_has_organization_id_rls_enabled_and_forced_and_the_tenant_policy()
    {
        var tables = await TenantTablesAsync();
        Assert.Contains("api_keys", tables);

        foreach (var table in tables)
        {
            var (hasOrgColumn, enabled, forced, hasPolicy) = await _tdb.RunAsync("aiep_migrator", null, null, async c =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    SELECT
                        EXISTS (SELECT 1 FROM information_schema.columns
                                WHERE table_schema = 'aiep' AND table_name = $1 AND column_name = $2
                                  AND data_type = 'uuid' AND is_nullable = 'NO'),
                        c.relrowsecurity,
                        c.relforcerowsecurity,
                        EXISTS (SELECT 1 FROM pg_policies p
                                WHERE p.schemaname = 'aiep' AND p.tablename = $1 AND p.policyname = 'tenant_isolation'
                                  AND p.cmd = 'ALL' AND p.roles = '{public}'
                                  AND p.qual LIKE '%app.organization_id%' AND p.with_check LIKE '%app.organization_id%')
                    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'aiep' AND c.relname = $1
                    """, c);
                command.Parameters.Add(new NpgsqlParameter { Value = table });
                command.Parameters.Add(new NpgsqlParameter { Value = OrgColumn(table) });
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
            });

            Assert.True(hasOrgColumn, $"{table}: missing NOT NULL uuid {OrgColumn(table)}");
            Assert.True(enabled, $"{table}: RLS not enabled");
            Assert.True(forced, $"{table}: RLS not forced");
            Assert.True(hasPolicy, $"{table}: missing tenant_isolation policy");
        }
    }

    [Fact]
    public async Task Subjects_has_rls_enabled_and_forced()
    {
        var (enabled, forced) = await _tdb.RunAsync("aiep_migrator", null, null, async c =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = 'aiep.subjects'::regclass", c);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (reader.GetBoolean(0), reader.GetBoolean(1));
        });

        Assert.True(enabled);
        Assert.True(forced);
    }

    [Fact]
    public async Task Service_roles_cannot_bypass_rls_and_own_nothing()
    {
        var offenders = await _tdb.RunAsync("aiep_migrator", null, null, c => TenantDb.ColumnAsync<string>(c,
            """
            SELECT rolname::text FROM pg_roles
            WHERE rolname IN ('aiep_api', 'aiep_worker', 'aiep_migrator') AND (rolbypassrls OR rolsuper)
            UNION ALL
            SELECT c.relname::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'aiep' AND pg_get_userbyid(c.relowner) <> 'aiep_migrator'
            """));

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("aiep_api")]
    [InlineData("aiep_worker")]
    public async Task Without_tenant_context_every_tenant_table_returns_no_rows(string role)
    {
        await Seed.TenantAsync(_tdb, "nocontext");

        foreach (var table in await TenantTablesAsync())
        {
            var count = await _tdb.RunAsync(role, null, null,
                c => TenantDb.ScalarAsync<long>(c, $"SELECT count(*) FROM aiep.{table}"));
            Assert.True(count == 0, $"{table}: {count} rows visible to {role} without tenant context");
        }
    }

    [Fact]
    public async Task With_tenant_context_every_tenant_table_returns_only_that_tenants_rows()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        foreach (var table in await TenantTablesAsync())
        {
            foreach (var (self, other) in new[] { (a, b), (b, a) })
            {
                var orgIds = await _tdb.RunAsync("aiep_api", self.OrganizationId, self.OwnerId,
                    c => TenantDb.ColumnAsync<Guid>(c, $"SELECT {OrgColumn(table)} FROM aiep.{table}"));

                // Non-empty proves the test is meaningful: Seed.TenantAsync must put a row in every tenant table.
                Assert.True(orgIds.Count > 0, $"{table}: no seeded rows; extend Seed.TenantAsync");
                Assert.All(orgIds, id => Assert.Equal(self.OrganizationId, id));

                var leaked = await _tdb.RunAsync("aiep_api", self.OrganizationId, self.OwnerId,
                    c => TenantDb.ScalarAsync<long>(c,
                        $"SELECT count(*) FROM aiep.{table} WHERE {OrgColumn(table)} = $1", other.OrganizationId));
                Assert.Equal(0, leaked);
            }
        }
    }

    [Fact]
    public async Task Updates_and_deletes_cannot_reach_another_tenants_rows()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var (updated, deleted, renamed) = await _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, async c => (
            await TenantDb.ExecuteAsync(c, "UPDATE aiep.workspaces SET name = 'pwned' WHERE id = $1", b.WorkspaceId),
            await TenantDb.ExecuteAsync(c, "DELETE FROM aiep.workspace_memberships WHERE organization_id = $1", b.OrganizationId),
            await TenantDb.ExecuteAsync(c, "UPDATE aiep.organizations SET name = 'pwned' WHERE id = $1", b.OrganizationId)));

        Assert.Equal((0, 0, 0), (updated, deleted, renamed));
        var name = await _tdb.RunAsync("aiep_api", b.OrganizationId, b.OwnerId,
            c => TenantDb.ScalarAsync<string>(c, "SELECT name FROM aiep.workspaces WHERE id = $1", b.WorkspaceId));
        Assert.Equal("Main", name);
    }

    [Fact]
    public async Task Inserting_a_row_for_another_tenant_is_rejected_by_the_policy()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                "INSERT INTO aiep.workspaces (id, organization_id, slug, name) VALUES ($1, $2, 'x', 'x')",
                Guid.CreateVersion7(), b.OrganizationId)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("row-level security", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_a_row_to_another_tenant_is_rejected_by_the_policy()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                "UPDATE aiep.audit_events SET organization_id = $1", b.OrganizationId)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_context_does_not_outlive_its_transaction_on_a_reused_connection()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");

        await using var connection = new NpgsqlConnection(db.ConnectionString("aiep_api"));
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await TenantDb.ExecuteAsync(connection,
                "SELECT set_config('app.organization_id', $1, true)", a.OrganizationId.ToString());
            Assert.Equal(1, await TenantDb.ScalarAsync<long>(connection, "SELECT count(*) FROM aiep.organizations"));
            await tx.CommitAsync();
        }

        Assert.Equal(0, await TenantDb.ScalarAsync<long>(connection, "SELECT count(*) FROM aiep.organizations"));
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_a_grant_on_another_tenants_workspace()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        // The FK check ignores RLS, so it sees B's workspace and still refuses the (A, B-workspace) pair.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.workspace_memberships (id, organization_id, workspace_id, subject_id, workspace_role)
                VALUES ($1, $2, $3, $4, 'Viewer')
                """,
                Guid.CreateVersion7(), a.OrganizationId, b.WorkspaceId, a.OwnerId)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("workspace_memberships_workspace_fk", ex.ConstraintName);
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_an_api_key_scoped_to_another_tenants_workspace()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.api_keys
                    (id, organization_id, workspace_id, key_id, secret_hash, environment, name, owner_subject_id, permissions)
                VALUES ($1, $2, $3, $4, $5, 'test', 'x', $6, ARRAY['runs:read'])
                """,
                Guid.CreateVersion7(), a.OrganizationId, b.WorkspaceId, TenantDb.NewKeyId(), new byte[32], a.OwnerId)));

        Assert.Equal("api_keys_workspace_fk", ex.ConstraintName);
    }

    [Fact]
    public async Task A_workspace_grant_requires_an_organization_membership_and_is_removed_with_it()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var carol = await Seed.SubjectAsync(_tdb, "carol");

        // No membership in A yet: the grant is refused.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.workspace_memberships (id, organization_id, workspace_id, subject_id, workspace_role)
                VALUES ($1, $2, $3, $4, 'Viewer')
                """,
                Guid.CreateVersion7(), a.OrganizationId, a.WorkspaceId, carol)));
        Assert.Equal("workspace_memberships_membership_fk", ex.ConstraintName);

        // Add the membership and the grant, then remove the membership: the grant goes with it.
        var remaining = await _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, async c =>
        {
            await TenantDb.ExecuteAsync(c,
                "INSERT INTO aiep.memberships (id, organization_id, subject_id, org_role) VALUES ($1, $2, $3, 'Member')",
                Guid.CreateVersion7(), a.OrganizationId, carol);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.workspace_memberships (id, organization_id, workspace_id, subject_id, workspace_role)
                VALUES ($1, $2, $3, $4, 'Viewer')
                """,
                Guid.CreateVersion7(), a.OrganizationId, a.WorkspaceId, carol);
            await TenantDb.ExecuteAsync(c,
                "DELETE FROM aiep.memberships WHERE organization_id = $1 AND subject_id = $2", a.OrganizationId, carol);
            return await TenantDb.ScalarAsync<long>(c,
                "SELECT count(*) FROM aiep.workspace_memberships WHERE subject_id = $1", carol);
        });
        Assert.Equal(0, remaining);
    }
}
