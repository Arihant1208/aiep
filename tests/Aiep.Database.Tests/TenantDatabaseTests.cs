using Aiep.Application.Tenancy;
using Aiep.Infrastructure;
using Aiep.Infrastructure.Persistence;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aiep.Database.Tests;

/// <summary>
/// The tenant-context unit of work (ADR-0006, ADR-0013), resolved through the same AddInfrastructure registration
/// the API and worker use, connected as aiep_api.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantDatabaseTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly TenantDb _tdb = new(db);
    private ServiceProvider _services = null!;
    private TenantDatabase _database = null!;

    public Task InitializeAsync()
    {
        _services = BuildServices(maxPoolSize: 10);
        _database = _services.GetRequiredService<TenantDatabase>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private ServiceProvider BuildServices(int maxPoolSize)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString("aiep_api", pooling: true))
        {
            MaxPoolSize = maxPoolSize,
        }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Aiep"] = connectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    private static TenantContext ContextOf(Tenant t) => TenantContext.ForOrganization(t.OrganizationId, t.OwnerId);

    private static Task<long> BackendPidAsync(TenantSession s) =>
        TenantDb.ScalarAsync<long>(s.Connection, "SELECT pg_backend_pid()::bigint");

    // ---- the only way in ------------------------------------------------------------------------------------

    [Fact]
    public void Hosts_cannot_resolve_the_raw_data_source_or_a_db_context()
    {
        Assert.NotNull(_services.GetService<TenantDatabase>());
        Assert.Null(_services.GetService<NpgsqlDataSource>());
        Assert.Null(_services.GetService<AiepDbContext>());
        Assert.DoesNotContain(typeof(AiepDbContext).GetConstructors(), c => c.IsPublic);
        Assert.DoesNotContain(typeof(TenantDatabase).GetConstructors(), c => c.IsPublic);
    }

    [Fact]
    public void Tenant_context_rejects_empty_identifiers()
    {
        Assert.Throws<ArgumentException>(() => TenantContext.ForOrganization(Guid.Empty, null));
        Assert.Throws<ArgumentException>(() => TenantContext.ForOrganization(Guid.CreateVersion7(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => TenantContext.ForSubject(Guid.Empty));
    }

    // ---- isolation through EF -------------------------------------------------------------------------------

    [Fact]
    public async Task Without_context_ef_queries_return_nothing()
    {
        await Seed.TenantAsync(_tdb, "nocontext");

        var counts = await _database.ExecuteAsync(TenantContext.None, async (s, ct) => new[]
        {
            await s.Db.Subjects.CountAsync(ct),
            await s.Db.Organizations.CountAsync(ct),
            await s.Db.Memberships.CountAsync(ct),
            await s.Db.Workspaces.CountAsync(ct),
            await s.Db.WorkspaceMemberships.CountAsync(ct),
            await s.Db.ApiKeys.CountAsync(ct),
            await s.Db.AuditEvents.CountAsync(ct),
        });

        Assert.All(counts, c => Assert.Equal(0, c));
    }

    [Fact]
    public async Task With_context_ef_sees_only_its_own_tenant_even_when_asking_for_another_by_id()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var (orgs, workspaces, foreign, foreignKey) = await _database.ExecuteAsync(ContextOf(a), async (s, ct) => (
            await s.Db.Organizations.Select(o => o.Id).ToListAsync(ct),
            await s.Db.Workspaces.Select(w => w.OrganizationId).Distinct().ToListAsync(ct),
            await s.Db.Workspaces.FindAsync([b.WorkspaceId], ct),
            await s.Db.ApiKeys.SingleOrDefaultAsync(k => k.KeyId == b.KeyId, ct)));

        Assert.Equal([a.OrganizationId], orgs);
        Assert.Equal([a.OrganizationId], workspaces);
        Assert.Null(foreign);
        Assert.Null(foreignKey);
    }

    [Fact]
    public async Task Ef_inserts_for_another_tenant_are_rejected_and_nothing_is_committed()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");
        var smuggled = Guid.CreateVersion7();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _database.ExecuteAsync(ContextOf(a), (s, _) =>
        {
            s.Db.Workspaces.Add(new Workspace { Id = smuggled, OrganizationId = b.OrganizationId, Slug = "smuggled", Name = "x" });
            return Task.CompletedTask;
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        var found = await _database.ExecuteAsync(ContextOf(b), (s, ct) => s.Db.Workspaces.AnyAsync(w => w.Id == smuggled, ct));
        Assert.False(found);
    }

    // ---- unit-of-work semantics ----------------------------------------------------------------------------

    [Fact]
    public async Task Changes_are_saved_and_committed_when_the_work_completes()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var id = Guid.CreateVersion7();

        await _database.ExecuteAsync(ContextOf(a), (s, _) =>
        {
            s.Db.Workspaces.Add(new Workspace { Id = id, OrganizationId = a.OrganizationId, Slug = "committed", Name = "Committed" });
            return Task.CompletedTask;
        });

        var workspace = await _database.ExecuteAsync(ContextOf(a), (s, ct) => s.Db.Workspaces.SingleAsync(w => w.Id == id, ct));
        Assert.Equal("committed", workspace.Slug);
        Assert.NotEqual(default, workspace.CreatedAt);
    }

    [Fact]
    public async Task An_exception_rolls_back_everything_including_saved_changes()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var id = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.ExecuteAsync(ContextOf(a), async (s, ct) =>
        {
            s.Db.Workspaces.Add(new Workspace { Id = id, OrganizationId = a.OrganizationId, Slug = "rolled-back", Name = "x" });
            await s.Db.SaveChangesAsync(ct);
            throw new InvalidOperationException("boom");
        }));

        var exists = await _database.ExecuteAsync(ContextOf(a), (s, ct) => s.Db.Workspaces.AnyAsync(w => w.Id == id, ct));
        Assert.False(exists);
    }

    // ---- pooled connections (threat model T2) --------------------------------------------------------------

    [Fact]
    public async Task Pool_of_one_alternating_tenants_never_carries_context_over()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");
        await using var services = BuildServices(maxPoolSize: 1);
        var database = services.GetRequiredService<TenantDatabase>();
        var pids = new HashSet<long>();

        for (var i = 0; i < 30; i++)
        {
            var tenant = (i % 3) switch { 0 => a, 1 => b, _ => null };
            var context = tenant is null ? TenantContext.None : ContextOf(tenant);

            var (pid, orgs, keys) = await database.ExecuteAsync(context, async (s, ct) => (
                await BackendPidAsync(s),
                await s.Db.Organizations.Select(o => o.Id).ToListAsync(ct),
                await s.Db.ApiKeys.Select(k => k.OrganizationId).ToListAsync(ct)));
            pids.Add(pid);

            if (tenant is null)
            {
                Assert.Empty(orgs);
                Assert.Empty(keys);
            }
            else
            {
                Assert.Equal([tenant.OrganizationId], orgs);
                Assert.All(keys, k => Assert.Equal(tenant.OrganizationId, k));
            }
        }

        // Proves the iterations really shared one physical connection.
        Assert.Single(pids);
    }

    [Fact]
    public async Task Pool_of_one_a_failed_unit_of_work_leaves_no_context_behind()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        await using var services = BuildServices(maxPoolSize: 1);
        var database = services.GetRequiredService<TenantDatabase>();

        var failedPid = 0L;
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.ExecuteAsync(ContextOf(a), async (s, _) =>
        {
            failedPid = await BackendPidAsync(s);
            throw new InvalidOperationException("boom");
        }));

        var (pid, orgs) = await database.ExecuteAsync(TenantContext.None, async (s, ct) =>
            (await BackendPidAsync(s), await s.Db.Organizations.CountAsync(ct)));
        Assert.Equal(failedPid, pid);
        Assert.Equal(0, orgs);
    }

    [Fact]
    public async Task Tenant_settings_are_transaction_local_on_the_same_connection()
    {
        // Pins our own rule (set_config(..., true)) without help from the pool: one unpooled connection, two
        // transactions. Mutation-checked: switching to session-level set_config makes this test fail.
        var a = await Seed.TenantAsync(_tdb, "alice");
        await using var connection = new NpgsqlConnection(db.ConnectionString("aiep_api"));
        await connection.OpenAsync();

        await using (var tx = await connection.BeginTransactionAsync())
        {
            await TenantDatabase.SetContextAsync(connection, tx, ContextOf(a), CancellationToken.None);
            Assert.Equal(a.OrganizationId.ToString(), await TenantDb.ScalarAsync<string>(connection,
                "SELECT current_setting('app.organization_id', true)"));
            await tx.CommitAsync();
        }

        var org = await TenantDb.ScalarAsync<string>(connection, "SELECT coalesce(current_setting('app.organization_id', true), '')");
        var subject = await TenantDb.ScalarAsync<string>(connection, "SELECT coalesce(current_setting('app.subject_id', true), '')");
        Assert.Equal((string.Empty, string.Empty), (org, subject));
    }

    [Fact]
    public async Task Pool_of_one_the_connection_carries_no_tenant_setting_after_a_unit_of_work()
    {
        // End-to-end property on the pooled connection, outside any unit of work. Two mechanisms give it: settings
        // are transaction-local (test above), and Npgsql resets session state (DISCARD ALL) when a connection
        // returns to the pool. Either alone keeps this test green.
        var a = await Seed.TenantAsync(_tdb, "alice");
        await using var services = BuildServices(maxPoolSize: 1);
        var database = services.GetRequiredService<TenantDatabase>();

        var pid = await database.ExecuteAsync(ContextOf(a), (s, _) => BackendPidAsync(s));

        await using var raw = await services.GetRequiredService<AiepDataSource>().OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(pid, await TenantDb.ScalarAsync<long>(raw, "SELECT pg_backend_pid()::bigint"));
        var org = await TenantDb.ScalarAsync<string>(raw, "SELECT coalesce(current_setting('app.organization_id', true), '')");
        var subject = await TenantDb.ScalarAsync<string>(raw, "SELECT coalesce(current_setting('app.subject_id', true), '')");
        Assert.Equal((string.Empty, string.Empty), (org, subject));
    }

    // ---- no way out of RLS for the service role -------------------------------------------------------------

    [Theory]
    [InlineData("SET ROLE aiep_migrator")]
    [InlineData("SET ROLE postgres")]
    [InlineData("SET SESSION AUTHORIZATION aiep_migrator")]
    [InlineData("ALTER ROLE aiep_api BYPASSRLS")]
    [InlineData("ALTER TABLE aiep.organizations NO FORCE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE aiep.organizations DISABLE ROW LEVEL SECURITY")]
    [InlineData("DROP POLICY tenant_isolation ON aiep.organizations")]
    public async Task The_api_role_cannot_escape_rls(string sql)
    {
        var a = await Seed.TenantAsync(_tdb, "alice");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => _database.ExecuteAsync(ContextOf(a),
            (s, _) => TenantDb.ExecuteAsync(s.Connection, sql)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Turning_row_security_off_makes_queries_fail_instead_of_bypassing_rls()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => _database.ExecuteAsync(ContextOf(a), async (s, ct) =>
        {
            await TenantDb.ExecuteAsync(s.Connection, "SET LOCAL row_security = off");
            return await s.Db.Organizations.CountAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    // ---- mappings match the schema (ADR-0013 drift check) ---------------------------------------------------

    [Fact]
    public async Task Ef_model_matches_the_database_columns_exactly()
    {
        var model = await _database.ExecuteAsync(TenantContext.None, (s, _) => Task.FromResult(s.Db.Model));
        var mappedTables = model.GetEntityTypes().Select(e => e.GetTableName()!).ToHashSet();

        var dbColumns = await _tdb.RunAsync("aiep_migrator", null, null, async c =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT table_name::text, column_name::text, is_nullable = 'YES' FROM information_schema.columns WHERE table_schema = 'aiep'", c);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string Table, string Column, bool Nullable)>();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            }

            return rows;
        });

        // Every table except migration history is mapped.
        Assert.Equal(
            dbColumns.Select(c => c.Table).Where(t => t != "schema_migrations").Distinct().Order(),
            mappedTables.Order());

        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            var expected = dbColumns.Where(c => c.Table == table).Select(c => (c.Column, c.Nullable)).Order().ToList();
            var actual = entity.GetProperties()
                .Select(p => (Column: p.GetColumnName(), Nullable: p.IsNullable)).Order().ToList();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Every_mapped_entity_materializes_real_rows()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");

        await _database.ExecuteAsync(ContextOf(a), async (s, ct) =>
        {
            Assert.Equal(a.OwnerId, (await s.Db.Subjects.SingleAsync(ct)).Id);
            Assert.Equal(a.OrganizationId, (await s.Db.Organizations.SingleAsync(ct)).Id);
            Assert.Equal("Owner", (await s.Db.Memberships.SingleAsync(ct)).OrgRole);
            Assert.Equal("main", (await s.Db.Workspaces.SingleAsync(ct)).Slug);
            Assert.Equal("WorkspaceAdmin", (await s.Db.WorkspaceMemberships.SingleAsync(ct)).WorkspaceRole);
            var key = await s.Db.ApiKeys.SingleAsync(ct);
            Assert.Equal(["runs:submit"], key.Permissions);
            Assert.Equal(32, key.SecretHash.Length);
            Assert.Equal("organization.created", (await s.Db.AuditEvents.SingleAsync(ct)).Action);
        });
    }
}
