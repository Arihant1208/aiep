using Npgsql;

namespace Aiep.Database.Tests;

/// <summary>
/// Per-table privileges and the SECURITY DEFINER bootstrap functions in V0001: what services can and cannot do
/// outside the tenant policy.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PrivilegeAndBootstrapTests(PostgresFixture db)
{
    private readonly TenantDb _tdb = new(db);

    private static async Task<string> SqlStateOf(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.SqlState;
    }

    // ---- audit_events: append-only -------------------------------------------------------------------------

    [Theory]
    [InlineData("aiep_api", "UPDATE aiep.audit_events SET reason = 'edited'")]
    [InlineData("aiep_api", "DELETE FROM aiep.audit_events")]
    [InlineData("aiep_worker", "UPDATE aiep.audit_events SET reason = 'edited'")]
    [InlineData("aiep_worker", "DELETE FROM aiep.audit_events")]
    public async Task Services_cannot_update_or_delete_audit_events(string role, string sql)
    {
        var a = await Seed.TenantAsync(_tdb, "audit");

        var state = await SqlStateOf(() => _tdb.RunAsync(role, a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c, sql)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    [Fact]
    public async Task Even_the_owner_cannot_update_audit_events_because_of_the_trigger()
    {
        var a = await Seed.TenantAsync(_tdb, "audit");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_migrator", a.OrganizationId, null, c => TenantDb.ExecuteAsync(c,
                "UPDATE aiep.audit_events SET reason = 'edited'")));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_worker_can_append_audit_events()
    {
        var a = await Seed.TenantAsync(_tdb, "audit");

        var inserted = await _tdb.RunAsync("aiep_worker", a.OrganizationId, null, c => TenantDb.ExecuteAsync(c,
            "INSERT INTO aiep.audit_events (id, organization_id, actor_type, action, outcome) VALUES ($1, $2, 'service', 'run.claimed', 'success')",
            Guid.CreateVersion7(), a.OrganizationId));

        Assert.Equal(1, inserted);
    }

    // ---- narrowed table privileges -------------------------------------------------------------------------

    [Theory]
    [InlineData("aiep_api", "DELETE FROM aiep.organizations")]
    [InlineData("aiep_api", "DELETE FROM aiep.workspaces")]
    [InlineData("aiep_api", "DELETE FROM aiep.api_keys")]
    [InlineData("aiep_worker", "UPDATE aiep.memberships SET org_role = 'Owner'")]
    [InlineData("aiep_worker", "UPDATE aiep.workspaces SET name = 'x'")]
    [InlineData("aiep_worker", "UPDATE aiep.api_keys SET revoked_at = now()")]
    [InlineData("aiep_worker", "DELETE FROM aiep.workspace_memberships")]
    [InlineData("aiep_worker", "UPDATE aiep.organizations SET name = 'x'")]
    public async Task Narrowed_privileges_are_enforced(string role, string sql)
    {
        var a = await Seed.TenantAsync(_tdb, "privs");

        var state = await SqlStateOf(() => _tdb.RunAsync(role, a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c, sql)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    [Fact]
    public async Task Services_cannot_insert_organizations_directly()
    {
        var subject = await Seed.SubjectAsync(_tdb, "direct");
        var org = Guid.CreateVersion7();

        var state = await SqlStateOf(() => _tdb.RunAsync("aiep_api", org, subject, c => TenantDb.ExecuteAsync(c,
            "INSERT INTO aiep.organizations (id, name) VALUES ($1, 'x')", org)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    // ---- subjects ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("aiep_api")]
    [InlineData("aiep_worker")]
    public async Task Services_cannot_write_subjects_directly(string role)
    {
        var subject = await Seed.SubjectAsync(_tdb, "direct");

        foreach (var sql in new[]
                 {
                     "INSERT INTO aiep.subjects (id, issuer, external_subject) VALUES (gen_random_uuid(), 'i', 's')",
                     "UPDATE aiep.subjects SET email = 'x@example.test'",
                     "DELETE FROM aiep.subjects",
                 })
        {
            var state = await SqlStateOf(() => _tdb.RunAsync(role, null, subject, c => TenantDb.ExecuteAsync(c, sql)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
        }
    }

    [Fact]
    public async Task Resolve_subject_is_stable_for_the_same_issuer_and_subject_and_refreshes_display_data()
    {
        var external = $"sub-{Guid.NewGuid():N}";
        const string Issuer = "https://idp.test/realms/aiep";

        async Task<Guid> Resolve(Guid newId, string email) => await _tdb.RunAsync("aiep_api", null, null,
            c => TenantDb.ScalarAsync<Guid>(c, "SELECT aiep.resolve_subject($1, $2, $3, 'Dee', $4)",
                newId, Issuer, external, email));

        var first = await Resolve(Guid.CreateVersion7(), "dee@example.test");
        var second = await Resolve(Guid.CreateVersion7(), "dee@example.test");
        var third = await Resolve(Guid.CreateVersion7(), "dee@new.example.test");

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        var email = await _tdb.RunAsync("aiep_api", null, first,
            c => TenantDb.ScalarAsync<string>(c, "SELECT email FROM aiep.subjects WHERE id = $1", first));
        Assert.Equal("dee@new.example.test", email);
    }

    [Fact]
    public async Task Same_subject_claim_from_a_different_issuer_is_a_different_subject()
    {
        var external = $"sub-{Guid.NewGuid():N}";

        var ids = new List<Guid>();
        foreach (var issuer in new[] { "https://idp-one.test", "https://idp-two.test" })
        {
            ids.Add(await _tdb.RunAsync("aiep_api", null, null, c => TenantDb.ScalarAsync<Guid>(c,
                "SELECT aiep.resolve_subject($1, $2, $3, NULL, NULL)", Guid.CreateVersion7(), issuer, external)));
        }

        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task Services_see_only_themselves_and_members_of_the_current_organization()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var noContext = await _tdb.RunAsync("aiep_api", null, null,
            c => TenantDb.ScalarAsync<long>(c, "SELECT count(*) FROM aiep.subjects"));
        var selfOnly = await _tdb.RunAsync("aiep_api", null, a.OwnerId,
            c => TenantDb.ColumnAsync<Guid>(c, "SELECT id FROM aiep.subjects"));
        var inOrgA = await _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId,
            c => TenantDb.ColumnAsync<Guid>(c, "SELECT id FROM aiep.subjects"));

        Assert.Equal(0, noContext);
        Assert.Equal([a.OwnerId], selfOnly);
        Assert.Equal([a.OwnerId], inOrgA);
        Assert.DoesNotContain(b.OwnerId, inOrgA);
    }

    // ---- create_organization -------------------------------------------------------------------------------

    [Fact]
    public async Task Create_organization_makes_the_authenticated_subject_the_owner_and_restores_the_context()
    {
        var subject = await Seed.SubjectAsync(_tdb, "founder");
        var org = Guid.CreateVersion7();

        var contextAfter = await _tdb.RunAsync("aiep_api", null, subject, async c =>
        {
            await TenantDb.ExecuteAsync(c, "SELECT aiep.create_organization($1, 'Founder Ltd', $2)", org, Guid.CreateVersion7());
            return await TenantDb.ScalarAsync<string>(c, "SELECT coalesce(current_setting('app.organization_id', true), '')");
        });

        Assert.Equal(string.Empty, contextAfter);
        var role = await _tdb.RunAsync("aiep_api", org, subject, c => TenantDb.ScalarAsync<string>(c,
            "SELECT org_role FROM aiep.memberships WHERE subject_id = $1", subject));
        Assert.Equal("Owner", role);
    }

    [Fact]
    public async Task Create_organization_requires_an_authenticated_subject()
    {
        var state = await SqlStateOf(() => _tdb.RunAsync("aiep_api", null, null, c => TenantDb.ExecuteAsync(c,
            "SELECT aiep.create_organization($1, 'x', $2)", Guid.CreateVersion7(), Guid.CreateVersion7())));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    [Fact]
    public async Task Create_organization_refuses_to_run_inside_an_existing_tenant_context()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");

        var state = await SqlStateOf(() => _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
            "SELECT aiep.create_organization($1, 'x', $2)", Guid.CreateVersion7(), Guid.CreateVersion7())));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    [Fact]
    public async Task Create_organization_cannot_be_used_to_join_an_existing_organization()
    {
        var victim = await Seed.TenantAsync(_tdb, "victim");
        var attacker = await Seed.SubjectAsync(_tdb, "attacker");

        var state = await SqlStateOf(() => _tdb.RunAsync("aiep_api", null, attacker, c => TenantDb.ExecuteAsync(c,
            "SELECT aiep.create_organization($1, 'hijack', $2)", victim.OrganizationId, Guid.CreateVersion7())));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, state);
        var attackerMemberships = await _tdb.RunAsync("aiep_api", victim.OrganizationId, victim.OwnerId,
            c => TenantDb.ScalarAsync<long>(c, "SELECT count(*) FROM aiep.memberships WHERE subject_id = $1", attacker));
        Assert.Equal(0, attackerMemberships);
    }

    // ---- find_api_key --------------------------------------------------------------------------------------

    [Fact]
    public async Task Find_api_key_resolves_the_owning_tenant_without_a_context()
    {
        var a = await Seed.TenantAsync(_tdb, "keys");

        var (org, owner) = await _tdb.RunAsync("aiep_api", null, null, async c =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT organization_id, owner_subject_id FROM aiep.find_api_key($1)", c);
            command.Parameters.Add(new NpgsqlParameter { Value = a.KeyId });
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var row = (reader.GetGuid(0), reader.GetGuid(1));
            Assert.False(await reader.ReadAsync());
            return row;
        });

        Assert.Equal(a.OrganizationId, org);
        Assert.Equal(a.OwnerId, owner);
    }

    [Fact]
    public async Task Find_api_key_returns_nothing_for_an_unknown_key()
    {
        var count = await _tdb.RunAsync("aiep_api", null, null,
            c => TenantDb.ScalarAsync<long>(c, "SELECT count(*) FROM aiep.find_api_key($1)", TenantDb.NewKeyId()));

        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("SELECT aiep.find_api_key('0123456789abcdef')")]
    [InlineData("SELECT aiep.resolve_subject(gen_random_uuid(), 'i', 's', NULL, NULL)")]
    [InlineData("SELECT aiep.create_organization(gen_random_uuid(), 'x', gen_random_uuid())")]
    public async Task The_worker_cannot_call_bootstrap_functions(string sql)
    {
        var state = await SqlStateOf(() => _tdb.RunAsync("aiep_worker", null, Guid.CreateVersion7(),
            c => TenantDb.ExecuteAsync(c, sql)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    [Fact]
    public async Task Bootstrap_functions_are_security_definer_with_a_pinned_search_path()
    {
        var unsafeFunctions = await _tdb.RunAsync("aiep_migrator", null, null, c => TenantDb.ColumnAsync<string>(c,
            """
            SELECT p.proname::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'aiep' AND p.prosecdef
              AND NOT coalesce(p.proconfig @> ARRAY['search_path=aiep, pg_temp'], false)
            """));

        Assert.Empty(unsafeFunctions);
    }
}
