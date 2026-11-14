using System.Security.Cryptography;
using Npgsql;

namespace Aiep.Testing;

/// <summary>
/// Test-only access helper: opens a connection as a service role, starts a transaction and sets the tenant context
/// with set_config(..., true), the same rule the production unit of work (Phase 1 step 3) must follow.
/// </summary>
public sealed class TenantDb(PostgresFixture db)
{
    public async Task<T> RunAsync<T>(
        string role, Guid? organizationId, Guid? subjectId, Func<NpgsqlConnection, Task<T>> work)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString(role));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.organization_id', @org, true), set_config('app.subject_id', @sub, true)",
            connection))
        {
            context.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
            context.Parameters.AddWithValue("sub", subjectId?.ToString() ?? string.Empty);
            await context.ExecuteNonQueryAsync();
        }

        var result = await work(connection);
        await transaction.CommitAsync();
        return result;
    }

    public Task RunAsync(string role, Guid? organizationId, Guid? subjectId, Func<NpgsqlConnection, Task> work) =>
        RunAsync(role, organizationId, subjectId, async c =>
        {
            await work(c);
            return true;
        });

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, params object?[] args)
    {
        await using var command = Command(connection, sql, args);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params object?[] args)
    {
        await using var command = Command(connection, sql, args);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<List<T>> ColumnAsync<T>(NpgsqlConnection connection, string sql, params object?[] args)
    {
        await using var command = Command(connection, sql, args);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<T>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }

    /// <summary>Positional parameters: $1, $2, ...</summary>
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object?[] args)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return command;
    }

    public static string NewKeyId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
}

/// <summary>One organization with its owner and one row in every tenant-owned table.</summary>
public sealed record Tenant(
    Guid OrganizationId,
    Guid OwnerId,
    Guid WorkspaceId,
    Guid ApiKeyId,
    string KeyId,
    Guid TaskDefinitionId,
    Guid TaskDefinitionVersionId,
    Guid RunId);

public static class Seed
{
    public static async Task<Guid> SubjectAsync(TenantDb tdb, string name)
    {
        return await tdb.RunAsync("aiep_api", null, null, c => TenantDb.ScalarAsync<Guid>(c,
            "SELECT aiep.resolve_subject($1, $2, $3, $4, $5)",
            Guid.CreateVersion7(), "https://idp.test/realms/aiep", $"{name}-{Guid.NewGuid():N}", name, $"{name}@example.test"));
    }

    /// <summary>
    /// Creates an organization owned by a new subject and inserts one row into every tenant-owned table, all as
    /// aiep_api through the same paths the API will use.
    /// </summary>
    public static async Task<Tenant> TenantAsync(TenantDb tdb, string name)
    {
        var owner = await SubjectAsync(tdb, name);
        var org = Guid.CreateVersion7();
        await tdb.RunAsync("aiep_api", null, owner, c => TenantDb.ExecuteAsync(c,
            "SELECT aiep.create_organization($1, $2, $3)", org, $"{name} org", Guid.CreateVersion7()));

        var workspace = Guid.CreateVersion7();
        var apiKey = Guid.CreateVersion7();
        var keyId = TenantDb.NewKeyId();
        await tdb.RunAsync("aiep_api", org, owner, async c =>
        {
            await TenantDb.ExecuteAsync(c,
                "INSERT INTO aiep.workspaces (id, organization_id, slug, name, created_by) VALUES ($1, $2, 'main', 'Main', $3)",
                workspace, org, owner);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.workspace_memberships (id, organization_id, workspace_id, subject_id, workspace_role)
                VALUES ($1, $2, $3, $4, 'WorkspaceAdmin')
                """,
                Guid.CreateVersion7(), org, workspace, owner);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.api_keys
                    (id, organization_id, workspace_id, key_id, secret_hash, environment, name, owner_subject_id, permissions)
                VALUES ($1, $2, $3, $4, $5, 'test', 'ci', $6, ARRAY['runs:submit'])
                """,
                apiKey, org, workspace, keyId, RandomNumberGenerator.GetBytes(32), owner);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.audit_events (id, organization_id, actor_type, actor_subject_id, action, outcome)
                VALUES ($1, $2, 'user', $3, 'organization.created', 'success')
                """,
                Guid.CreateVersion7(), org, owner);
        });

        // V0002: one row in every task-execution table, through the same roles the API and worker use.
        var definition = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        var run = Guid.CreateVersion7();
        await tdb.RunAsync("aiep_api", org, owner, async c =>
        {
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_definitions
                    (id, organization_id, workspace_id, slug, name, current_version, created_by)
                VALUES ($1, $2, $3, 'echo', 'Echo', 1, $4)
                """,
                definition, org, workspace, owner);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_definition_versions
                    (id, organization_id, workspace_id, task_definition_id, version, status,
                     input_schema, output_schema, handler_type, handler_version, model_provider, model_name,
                     published_at, created_by)
                VALUES ($1, $2, $3, $4, 1, 'published',
                        '{}'::jsonb, '{}'::jsonb, 'echo', '1', 'fake', 'fake-1', now(), $5)
                """,
                version, org, workspace, definition, owner);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.task_runs
                    (id, organization_id, workspace_id, task_definition_version_id, status, max_attempts,
                     input, next_attempt_at, created_by)
                VALUES ($1, $2, $3, $4, 'Queued', 3, '{}'::jsonb, now(), $5)
                """,
                run, org, workspace, version, owner);
            await TenantDb.ExecuteAsync(c,
                """
                INSERT INTO aiep.idempotency_keys
                    (id, organization_id, workspace_id, caller_id, key_hash, request_hash, run_id, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, now() + interval '1 day')
                """,
                Guid.CreateVersion7(), org, workspace, owner,
                RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), run);
        });

        // Attempts are written by the worker role only (aiep_api has no INSERT on task_run_attempts).
        await tdb.RunAsync("aiep_worker", org, owner, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.task_run_attempts
                (id, organization_id, workspace_id, run_id, attempt_number, status, worker_id)
            VALUES ($1, $2, $3, $4, 1, 'running', 'seed')
            """,
            Guid.CreateVersion7(), org, workspace, run));

        return new Tenant(org, owner, workspace, apiKey, keyId, definition, version, run);
    }
}
