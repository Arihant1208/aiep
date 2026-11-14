using System.Security.Cryptography;
using Npgsql;

namespace Aiep.Database.Tests;

/// <summary>
/// V0002 task-execution schema: the immutability trigger on published versions, the narrowed per-role privileges for
/// the queue, and the composite foreign keys that keep runs and attempts inside one tenant. The catalog-driven tests
/// in <see cref="TenantIsolationTests"/> already prove RLS, FORCE and the policy on these tables.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TaskExecutionSchemaTests(PostgresFixture db)
{
    private readonly TenantDb _tdb = new(db);

    private static async Task<string> SqlStateOf(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.SqlState;
    }

    // ---- immutable published versions ----------------------------------------------------------------------

    [Fact]
    public async Task A_draft_version_can_be_edited_then_published_but_not_changed_afterwards()
    {
        var a = await Seed.TenantAsync(_tdb, "versions");
        var definition = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();

        await _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, async c =>
        {
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_definitions (id, organization_id, workspace_id, slug, name, created_by)
                VALUES ($1, $2, $3, 'draft-def', 'Draft', $4)
                """,
                definition, a.OrganizationId, a.WorkspaceId, a.OwnerId);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_definition_versions
                    (id, organization_id, workspace_id, task_definition_id, version, status,
                     input_schema, output_schema, handler_type, handler_version, model_provider, model_name, created_by)
                VALUES ($1, $2, $3, $4, 1, 'draft',
                        '{}'::jsonb, '{}'::jsonb, 'echo', '1', 'fake', 'fake-1', $5)
                """,
                version, a.OrganizationId, a.WorkspaceId, definition, a.OwnerId);

            // A draft is mutable.
            var edited = await TenantDb.ExecuteAsync(c,
                "UPDATE aiep.task_definition_versions SET model_name = 'fake-2' WHERE id = $1", version);
            Assert.Equal(1, edited);

            // Publishing is the transition that stamps published_at; the trigger allows it (OLD.published_at is NULL).
            var published = await TenantDb.ExecuteAsync(c,
                "UPDATE aiep.task_definition_versions SET status = 'published', published_at = now() WHERE id = $1",
                version);
            Assert.Equal(1, published);
        });

        // After publish, any update is rejected by the trigger.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                "UPDATE aiep.task_definition_versions SET model_name = 'fake-3' WHERE id = $1", version)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("immutable", ex.MessageText, StringComparison.Ordinal);
    }

    // ---- narrowed privileges for the queue -----------------------------------------------------------------

    // {org}, {ws}, {ver} are replaced with the seeded ids so each statement is self-contained (no bind parameters,
    // which would have to match each query's placeholder count). The privilege check fires regardless of the values.
    [Theory]
    [InlineData("aiep_api", "INSERT INTO aiep.task_run_attempts (id, organization_id, workspace_id, run_id, attempt_number, worker_id) VALUES (gen_random_uuid(), '{org}', '{ws}', gen_random_uuid(), 2, 'x')")]
    [InlineData("aiep_api", "DELETE FROM aiep.task_runs")]
    [InlineData("aiep_api", "DELETE FROM aiep.task_definitions")]
    [InlineData("aiep_api", "UPDATE aiep.idempotency_keys SET expires_at = now()")]
    [InlineData("aiep_worker", "INSERT INTO aiep.task_runs (id, organization_id, workspace_id, task_definition_version_id, max_attempts, input) VALUES (gen_random_uuid(), '{org}', '{ws}', '{ver}', 3, '{}'::jsonb)")]
    [InlineData("aiep_worker", "UPDATE aiep.task_definition_versions SET model_name = 'x'")]
    [InlineData("aiep_worker", "INSERT INTO aiep.task_definitions (id, organization_id, workspace_id, slug, name) VALUES (gen_random_uuid(), '{org}', '{ws}', 'x', 'x')")]
    [InlineData("aiep_worker", "DELETE FROM aiep.task_run_attempts")]
    public async Task Narrowed_queue_privileges_are_enforced(string role, string sql)
    {
        var a = await Seed.TenantAsync(_tdb, "queueprivs");
        sql = sql
            .Replace("{org}", a.OrganizationId.ToString(), StringComparison.Ordinal)
            .Replace("{ws}", a.WorkspaceId.ToString(), StringComparison.Ordinal)
            .Replace("{ver}", a.TaskDefinitionVersionId.ToString(), StringComparison.Ordinal);

        var state = await SqlStateOf(() => _tdb.RunAsync(role, a.OrganizationId, a.OwnerId, c =>
            TenantDb.ExecuteAsync(c, sql)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    [Fact]
    public async Task The_worker_can_claim_a_run_and_record_an_attempt()
    {
        var a = await Seed.TenantAsync(_tdb, "claim");
        var run = Guid.CreateVersion7();

        // A fresh Queued run authored by the API (the seed run already has a 'running' attempt).
        await _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.task_runs
                (id, organization_id, workspace_id, task_definition_version_id, status, max_attempts, input, next_attempt_at)
            VALUES ($1, $2, $3, $4, 'Queued', 3, '{}'::jsonb, now())
            """,
            run, a.OrganizationId, a.WorkspaceId, a.TaskDefinitionVersionId));

        var (claimed, attempts) = await _tdb.RunAsync("aiep_worker", a.OrganizationId, a.OwnerId, async c =>
        {
            var updated = await TenantDb.ExecuteAsync(c,
                """
                UPDATE aiep.task_runs
                SET status = 'Running', attempt_count = attempt_count + 1,
                    lease_owner = 'worker-1', lease_expires_at = now() + interval '60 seconds', started_at = now()
                WHERE id = $1 AND status = 'Queued'
                """,
                run);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_run_attempts (id, organization_id, workspace_id, run_id, attempt_number, worker_id)
                VALUES ($1, $2, $3, $4, 1, 'worker-1')
                """,
                Guid.CreateVersion7(), a.OrganizationId, a.WorkspaceId, run);
            var count = await TenantDb.ScalarAsync<long>(c,
                "SELECT count(*) FROM aiep.task_run_attempts WHERE run_id = $1", run);
            return (updated, count);
        });

        Assert.Equal(1, claimed);
        Assert.Equal(1, attempts);
    }

    // ---- composite foreign keys keep children inside one tenant --------------------------------------------

    [Fact]
    public async Task A_run_cannot_reference_another_tenants_definition_version()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_runs
                    (id, organization_id, workspace_id, task_definition_version_id, status, max_attempts, input)
                VALUES ($1, $2, $3, $4, 'Queued', 3, '{}'::jsonb)
                """,
                Guid.CreateVersion7(), a.OrganizationId, a.WorkspaceId, b.TaskDefinitionVersionId)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("task_runs_version_fk", ex.ConstraintName);
    }

    [Fact]
    public async Task An_attempt_cannot_reference_another_tenants_run()
    {
        var a = await Seed.TenantAsync(_tdb, "alice");
        var b = await Seed.TenantAsync(_tdb, "bob");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _tdb.RunAsync("aiep_worker", a.OrganizationId, a.OwnerId, c => TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_run_attempts (id, organization_id, workspace_id, run_id, attempt_number, worker_id)
                VALUES ($1, $2, $3, $4, 9, 'worker-1')
                """,
                Guid.CreateVersion7(), a.OrganizationId, a.WorkspaceId, b.RunId)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("task_run_attempts_run_fk", ex.ConstraintName);
    }

    [Fact]
    public async Task The_same_idempotency_key_for_one_caller_and_workspace_cannot_be_inserted_twice()
    {
        var a = await Seed.TenantAsync(_tdb, "idem");
        var keyHash = RandomNumberGenerator.GetBytes(32);

        var state = await SqlStateOf(() => _tdb.RunAsync("aiep_api", a.OrganizationId, a.OwnerId, async c =>
        {
            for (var i = 0; i < 2; i++)
            {
                await TenantDb.ExecuteAsync(c,
                    """
                    INSERT INTO aiep.idempotency_keys
                        (id, organization_id, workspace_id, caller_id, key_hash, request_hash, run_id, expires_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, now() + interval '1 day')
                    """,
                    Guid.CreateVersion7(), a.OrganizationId, a.WorkspaceId, a.OwnerId,
                    keyHash, RandomNumberGenerator.GetBytes(32), a.RunId);
            }
        }));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, state);
    }
}
