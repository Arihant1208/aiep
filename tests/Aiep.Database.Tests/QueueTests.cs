using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aiep.Database.Tests;

/// <summary>
/// The PostgreSQL work queue (ADR-0005), exercised through <see cref="IRunQueue"/> as the API role (enqueue) and the
/// worker role (claim / heartbeat / settle / reconcile). Each test runs in its own migrated database, because claim and
/// reconcile are cross-tenant and would otherwise see other tests' rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueTests(PostgresFixture db) : IAsyncLifetime
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
        _tenant = await Seed.TenantAsync(_tdb, "q");
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

    private IRunQueue ApiQueue => _api.GetRequiredService<IRunQueue>();

    private IRunQueue WorkerQueue => _worker.GetRequiredService<IRunQueue>();

    private TenantContext OrgContext => TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId);

    private Task<Guid> EnqueueAsync(int maxAttempts = 3, string input = "{}") =>
        ApiQueue.EnqueueAsync(OrgContext,
            new EnqueueRunRequest(_tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, input, maxAttempts));

    private Task<string> RunStatusAsync(Guid runId) =>
        _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId,
            c => TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId));

    private Task ForceLeaseExpiryAsync(Guid runId) =>
        _tdb.RunAsync("aiep_worker", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            "UPDATE aiep.task_runs SET lease_expires_at = now() - interval '1 minute' WHERE id = $1", runId));

    // ---- enqueue and claim ---------------------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_inserts_a_queued_run_that_is_eligible_now()
    {
        var runId = await EnqueueAsync();

        var (status, eligible) = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c => (
            await TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<bool>(c, "SELECT next_attempt_at <= now() FROM aiep.task_runs WHERE id = $1", runId)));

        Assert.Equal("Queued", status);
        Assert.True(eligible);
    }

    [Fact]
    public async Task Claim_moves_the_run_to_running_with_a_lease_and_opens_an_attempt()
    {
        var runId = await EnqueueAsync();

        var claimed = await WorkerQueue.ClaimAsync("worker-1");

        Assert.NotNull(claimed);
        Assert.Equal(runId, claimed!.RunId);
        Assert.Equal(_tenant.OrganizationId, claimed.OrganizationId);
        Assert.Equal(1, claimed.AttemptNumber);
        Assert.Equal("completion", claimed.HandlerType);
        Assert.Equal("fake", claimed.ModelProvider);

        var (status, leaseOwner, attempts) = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c => (
            await TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<string>(c, "SELECT lease_owner FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<long>(c, "SELECT count(*) FROM aiep.task_run_attempts WHERE run_id = $1", runId)));

        Assert.Equal("Running", status);
        Assert.Equal("worker-1", leaseOwner);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Claim_returns_null_when_nothing_is_eligible()
    {
        // The seeded run is terminal, so an empty queue has nothing to claim.
        Assert.Null(await WorkerQueue.ClaimAsync("worker-1"));
    }

    [Fact]
    public async Task Concurrent_claims_never_claim_the_same_run_twice()
    {
        var first = await EnqueueAsync();
        var second = await EnqueueAsync();

        var claims = await Task.WhenAll(
            WorkerQueue.ClaimAsync("w1"),
            WorkerQueue.ClaimAsync("w2"),
            WorkerQueue.ClaimAsync("w3"),
            WorkerQueue.ClaimAsync("w4"));

        var claimedIds = claims.Where(c => c is not null).Select(c => c!.RunId).ToList();
        Assert.Equal(2, claimedIds.Count);
        Assert.Equal(new[] { first, second }.Order(), claimedIds.Order());
    }

    // ---- heartbeat -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_extends_the_lease_while_the_run_is_ours()
    {
        await EnqueueAsync();
        var claimed = await WorkerQueue.ClaimAsync("worker-1");

        var beat = await WorkerQueue.HeartbeatAsync(claimed!);

        Assert.True(beat.LeaseHeld);
        Assert.False(beat.CancelRequested);
        var leaseExpires = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c =>
            TenantDb.ScalarAsync<DateTime>(c, "SELECT lease_expires_at FROM aiep.task_runs WHERE id = $1", claimed!.RunId));
        Assert.True(leaseExpires >= claimed!.LeaseExpiresAt.UtcDateTime);
    }

    [Fact]
    public async Task Heartbeat_reports_a_lost_lease_after_the_run_is_settled()
    {
        await EnqueueAsync();
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        await WorkerQueue.SettleAsync(Succeeded(claimed!));

        var beat = await WorkerQueue.HeartbeatAsync(claimed!);

        Assert.False(beat.LeaseHeld);
    }

    [Fact]
    public async Task Heartbeat_reports_a_requested_cancellation()
    {
        await EnqueueAsync();
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            "UPDATE aiep.task_runs SET cancel_requested_at = now() WHERE id = $1", claimed!.RunId));

        var beat = await WorkerQueue.HeartbeatAsync(claimed!);

        Assert.True(beat.LeaseHeld);
        Assert.True(beat.CancelRequested);
    }

    // ---- settle --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Settle_success_persists_the_output_and_closes_the_attempt_exactly_once()
    {
        var runId = await EnqueueAsync();
        var claimed = await WorkerQueue.ClaimAsync("worker-1");

        await WorkerQueue.SettleAsync(Succeeded(claimed!, output: "{\"text\":\"ok\"}"));

        var (status, output, attemptStatus) = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c => (
            await TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<string>(c, "SELECT output::text FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_run_attempts WHERE id = $1", claimed!.AttemptId)));

        Assert.Equal("Succeeded", status);
        Assert.Contains("ok", output);
        Assert.Equal("succeeded", attemptStatus);

        // A duplicate/late settle that lost the lease must not overwrite the terminal outcome.
        await WorkerQueue.SettleAsync(Succeeded(claimed!) with { TargetStatus = TaskRunStatus.Failed, AttemptStatus = "failed" });
        Assert.Equal("Succeeded", await RunStatusAsync(runId));
    }

    [Fact]
    public async Task Settle_retry_returns_the_run_to_the_queue_and_it_can_be_claimed_again()
    {
        var runId = await EnqueueAsync();
        var first = await WorkerQueue.ClaimAsync("worker-1");

        await WorkerQueue.SettleAsync(new RunSettlement(
            first!.OrganizationId, first.WorkspaceId, first.RunId, first.AttemptId, first.WorkerId,
            TaskRunStatus.RetryWait, "failed",
            ErrorCategory: ErrorCategory.Transient, ErrorMessage: "temporary", NextAttemptAt: DateTimeOffset.UtcNow));

        Assert.Equal("RetryWait", await RunStatusAsync(runId));

        var second = await WorkerQueue.ClaimAsync("worker-2");
        Assert.NotNull(second);
        Assert.Equal(runId, second!.RunId);
        Assert.Equal(2, second.AttemptNumber);
    }

    // ---- reconcile -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_returns_an_expired_lease_to_the_queue_when_attempts_remain()
    {
        var runId = await EnqueueAsync(maxAttempts: 3);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        await ForceLeaseExpiryAsync(claimed!.RunId);

        var recovered = await WorkerQueue.ReconcileExpiredLeasesAsync();

        Assert.Equal(1, recovered);
        Assert.Equal("RetryWait", await RunStatusAsync(runId));
        var attemptStatus = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c =>
            TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_run_attempts WHERE id = $1", claimed.AttemptId));
        Assert.Equal("lease_expired", attemptStatus);
    }

    [Fact]
    public async Task Reconcile_dead_letters_an_expired_lease_when_attempts_are_exhausted()
    {
        var runId = await EnqueueAsync(maxAttempts: 1);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        await ForceLeaseExpiryAsync(claimed!.RunId);

        var recovered = await WorkerQueue.ReconcileExpiredLeasesAsync();

        Assert.Equal(1, recovered);
        Assert.Equal("DeadLettered", await RunStatusAsync(runId));
    }

    // ---- privileges ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_api_role_cannot_claim_or_reconcile()
    {
        await EnqueueAsync();

        await Assert.ThrowsAsync<PostgresException>(() => ApiQueue.ClaimAsync("worker-1"));
        await Assert.ThrowsAsync<PostgresException>(() => ApiQueue.ReconcileExpiredLeasesAsync());
    }

    // ---- LISTEN/NOTIFY -------------------------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_signals_a_waiting_worker()
    {
        var wait = WorkerQueue.WaitForEnqueueSignalAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        await EnqueueAsync();

        Assert.True(await wait);
    }

    // ---- idempotency ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_idempotency_key_maps_one_payload_to_one_run()
    {
        var store = _api.GetRequiredService<IIdempotencyStore>();
        var caller = _tenant.OwnerId;
        var runId = _tenant.RunId; // an existing run, for the run_id foreign key

        Assert.Null((await store.LookupAsync(OrgContext, _tenant.WorkspaceId, caller, "key-1", "payload")).Existing);
        Assert.True(await store.TryRecordAsync(OrgContext, _tenant.WorkspaceId, caller, "key-1", "payload", runId, TimeSpan.FromHours(24)));

        var samePayload = await store.LookupAsync(OrgContext, _tenant.WorkspaceId, caller, "key-1", "payload");
        Assert.Equal(runId, samePayload.Existing);
        Assert.True(samePayload.PayloadMatches);

        var differentPayload = await store.LookupAsync(OrgContext, _tenant.WorkspaceId, caller, "key-1", "other");
        Assert.Equal(runId, differentPayload.Existing);
        Assert.False(differentPayload.PayloadMatches);

        // A second record for the same key loses the race.
        Assert.False(await store.TryRecordAsync(
            OrgContext, _tenant.WorkspaceId, caller, "key-1", "payload", runId, TimeSpan.FromHours(24)));
    }

    private static RunSettlement Succeeded(ClaimedRun run, string output = "{}") => new(
        run.OrganizationId, run.WorkspaceId, run.RunId, run.AttemptId, run.WorkerId,
        TaskRunStatus.Succeeded, "succeeded", OutputJson: output,
        Usage: new ModelUsage(10, 20, TimeSpan.FromMilliseconds(5)));
}
