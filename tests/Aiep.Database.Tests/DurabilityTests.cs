using Aiep.Application.Execution;
using Aiep.Application.Execution.Handlers;
using Aiep.Application.Execution.Providers;
using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aiep.Database.Tests;

/// <summary>
/// The Phase 2 exit criterion (Spec §22, ADR-0005): submit → queue → execute → persist, including after a simulated
/// worker crash, with exactly one terminal outcome. Runs end to end over the real queue, reconciler and executor against
/// the fake provider, in an isolated migrated database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DurabilityTests(PostgresFixture db) : IAsyncLifetime
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
        _tenant = await Seed.TenantAsync(_tdb, "d");
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

    private IRunStore ApiRuns => _api.GetRequiredService<IRunStore>();

    private RunExecutor Executor() =>
        new(WorkerQueue, new HandlerRegistry([new CompletionHandler([new FakeModelProvider()])]), TimeProvider.System);

    private TenantContext OrgContext => TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId);

    private Task<string> RunStatusAsync(Guid runId) =>
        _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId,
            c => TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId));

    private Task<List<string>> AttemptStatusesAsync(Guid runId) =>
        _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ColumnAsync<string>(c,
            "SELECT status FROM aiep.task_run_attempts WHERE run_id = $1 ORDER BY attempt_number", runId));

    private Task ForceLeaseExpiryAsync(Guid runId) =>
        _tdb.RunAsync("aiep_worker", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            "UPDATE aiep.task_runs SET lease_expires_at = now() - interval '1 minute' WHERE id = $1", runId));

    [Fact]
    public async Task Submit_queue_execute_persist_works_end_to_end()
    {
        var submitted = await ApiRuns.SubmitAsync(OrgContext, new SubmitRunRequest(
            _tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, "\"hello\"", MaxAttempts: 3,
            CallerId: _tenant.OwnerId, IdempotencyKey: null, Traceparent: null, CreatedBy: _tenant.OwnerId),
            TimeSpan.FromHours(24));
        Assert.Equal(SubmitStatus.Created, submitted.Status);
        var runId = submitted.Run!.Id;

        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        var outcome = await Executor().ExecuteAsync(claimed!, CancellationToken.None);

        Assert.Equal(TaskRunStatus.Succeeded, outcome);
        Assert.Equal(runId, claimed!.RunId);
        var run = await ApiRuns.GetRunAsync(_tenant.OrganizationId, _tenant.OwnerId, _tenant.WorkspaceId, runId);
        Assert.Equal("Succeeded", run!.Status);
        Assert.Contains("echo:fake-1", run.Output);
        Assert.Equal(["succeeded"], await AttemptStatusesAsync(runId));
    }

    [Fact]
    public async Task A_worker_crash_mid_run_recovers_and_completes_exactly_once()
    {
        var runId = await ApiQueue.EnqueueAsync(OrgContext, new EnqueueRunRequest(
            _tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, "\"hello\"", MaxAttempts: 3));

        // Worker 1 claims the run, then "crashes": it never heartbeats or settles.
        var crashed = await WorkerQueue.ClaimAsync("worker-1");
        Assert.Equal(runId, crashed!.RunId);
        Assert.Equal(1, crashed.AttemptNumber);

        // The lease expires and the reconciler returns the run to the queue, recording the abandoned attempt.
        await ForceLeaseExpiryAsync(runId);
        Assert.Equal(1, await WorkerQueue.ReconcileExpiredLeasesAsync());
        Assert.Equal("RetryWait", await RunStatusAsync(runId));

        // Worker 2 claims the recovered run and finishes it.
        var recovered = await WorkerQueue.ClaimAsync("worker-2");
        Assert.Equal(runId, recovered!.RunId);
        Assert.Equal(2, recovered.AttemptNumber);
        Assert.Equal(TaskRunStatus.Succeeded, await Executor().ExecuteAsync(recovered, CancellationToken.None));

        // Exactly one terminal outcome, and both attempts are on record.
        Assert.Equal("Succeeded", await RunStatusAsync(runId));
        Assert.Equal(["lease_expired", "succeeded"], await AttemptStatusesAsync(runId));

        // The crashed worker coming back and trying to settle its old attempt is discarded (lease lost).
        await WorkerQueue.SettleAsync(new RunSettlement(
            crashed.OrganizationId, crashed.WorkspaceId, crashed.RunId, crashed.AttemptId, crashed.WorkerId,
            TaskRunStatus.Failed, "failed", ErrorCategory: ErrorCategory.Transient, ErrorMessage: "late"));
        Assert.Equal("Succeeded", await RunStatusAsync(runId));
    }

    [Fact]
    public async Task A_resubmission_with_the_same_idempotency_key_returns_the_same_run()
    {
        var request = new SubmitRunRequest(
            _tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, "\"same\"", MaxAttempts: 3,
            CallerId: _tenant.OwnerId, IdempotencyKey: "key-1", Traceparent: null, CreatedBy: _tenant.OwnerId);

        var first = await ApiRuns.SubmitAsync(OrgContext, request, TimeSpan.FromHours(24));
        var second = await ApiRuns.SubmitAsync(OrgContext, request, TimeSpan.FromHours(24));

        Assert.Equal(SubmitStatus.Created, first.Status);
        Assert.Equal(SubmitStatus.Existing, second.Status);
        Assert.Equal(first.Run!.Id, second.Run!.Id);

        // The duplicate submission created no second queued run (the seed run is terminal).
        var queued = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ScalarAsync<long>(c,
            "SELECT count(*) FROM aiep.task_runs WHERE task_definition_version_id = $1 AND status = 'Queued'",
            _tenant.TaskDefinitionVersionId));
        Assert.Equal(1, queued);
    }
}
