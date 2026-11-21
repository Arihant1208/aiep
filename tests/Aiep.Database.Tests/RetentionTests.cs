using System.Security.Cryptography;
using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aiep.Database.Tests;

/// <summary>
/// Retention cleanup (Phase 3, ADR-0015): the cross-tenant purge removes expired idempotency keys and terminal runs past
/// the window with their attempts, and leaves live and recent data alone. Each test runs in its own migrated database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionTests(PostgresFixture db) : IAsyncLifetime
{
    private string _database = null!;
    private ServiceProvider _api = null!;
    private ServiceProvider _worker = null!;
    private TenantDb _tdb = null!;
    private Tenant _tenant = null!;

    public async Task InitializeAsync()
    {
        _database = await db.CreateMigratedDatabaseAsync();
        _api = BuildServices("aiep_api");
        _worker = BuildServices("aiep_worker");
        _tdb = new TenantDb(db, _database);
        _tenant = await Seed.TenantAsync(_tdb, "r");
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _worker.DisposeAsync();
    }

    private ServiceProvider BuildServices(string role)
    {
        var connectionString = db.ConnectionString(role, _database, pooling: true);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Aiep"] = connectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    private IRunQueue WorkerQueue => _worker.GetRequiredService<IRunQueue>();

    private IRunQueue ApiQueue => _api.GetRequiredService<IRunQueue>();

    private Task<long> CountAsync(string sql, params object?[] args) =>
        _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ScalarAsync<long>(c, sql, args));

    private async Task<Guid> InsertTerminalRunAsync(string completedAtSql)
    {
        var runId = Guid.CreateVersion7();
        var insert = """
            INSERT INTO aiep.task_runs
                (id, organization_id, workspace_id, task_definition_version_id, status, attempt_count, max_attempts,
                 input, completed_at)
            VALUES ($1, $2, $3, $4, 'Succeeded', 1, 3, '{}'::jsonb, COMPLETED_AT)
            """.Replace("COMPLETED_AT", completedAtSql, StringComparison.Ordinal);
        await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId,
            c => TenantDb.ExecuteAsync(c, insert,
                runId, _tenant.OrganizationId, _tenant.WorkspaceId, _tenant.TaskDefinitionVersionId));
        await _tdb.RunAsync("aiep_worker", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.task_run_attempts (id, organization_id, workspace_id, run_id, attempt_number, status, worker_id, ended_at)
            VALUES ($1, $2, $3, $4, 1, 'succeeded', 'seed', now())
            """,
            Guid.CreateVersion7(), _tenant.OrganizationId, _tenant.WorkspaceId, runId));
        return runId;
    }

    [Fact]
    public async Task Purge_removes_expired_idempotency_keys_and_keeps_live_ones()
    {
        async Task InsertKeyAsync(string expiresAtSql) =>
            await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
                $"""
                INSERT INTO aiep.idempotency_keys
                    (id, organization_id, workspace_id, caller_id, key_hash, request_hash, run_id, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, {expiresAtSql})
                """,
                Guid.CreateVersion7(), _tenant.OrganizationId, _tenant.WorkspaceId, _tenant.OwnerId,
                RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), _tenant.RunId));

        await InsertKeyAsync("now() - interval '1 hour'");
        await InsertKeyAsync("now() + interval '1 hour'");

        var purged = await WorkerQueue.PurgeExpiredAsync(TimeSpan.FromDays(3650));

        Assert.True(purged.IdempotencyKeysDeleted >= 1);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM aiep.idempotency_keys WHERE expires_at < now()"));
        Assert.True(await CountAsync("SELECT count(*) FROM aiep.idempotency_keys WHERE expires_at > now()") >= 1);
    }

    [Fact]
    public async Task Purge_removes_old_terminal_runs_with_their_attempts_and_keeps_recent_and_active_runs()
    {
        var oldRun = await InsertTerminalRunAsync("now() - interval '60 days'");
        var recentRun = await InsertTerminalRunAsync("now()");
        var activeRun = await ApiQueue.EnqueueAsync(OrgContext(), new EnqueueRunRequest(
            _tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, "{}", 3));

        var purged = await WorkerQueue.PurgeExpiredAsync(TimeSpan.FromDays(30));

        Assert.Equal(1, purged.RunsDeleted);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM aiep.task_runs WHERE id = $1", oldRun));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM aiep.task_run_attempts WHERE run_id = $1", oldRun));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM aiep.task_runs WHERE id = $1", recentRun));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM aiep.task_runs WHERE id = $1", activeRun));
    }

    [Fact]
    public async Task The_api_role_cannot_purge()
    {
        await Assert.ThrowsAsync<PostgresException>(() => ApiQueue.PurgeExpiredAsync(TimeSpan.FromDays(30)));
    }

    private TenantContext OrgContext() => TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId);
}
