using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Execution.Handlers;
using Aiep.Application.Execution.Providers;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aiep.Database.Tests;

/// <summary>
/// Reserve-then-settle cost governance (Phase 4, threat T12): a run reserves its estimated cost at submission and is
/// refused when it would exceed the organization budget; the worker settles the actual cost at completion. Isolated DB.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuotaTests(PostgresFixture db) : IAsyncLifetime
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

    private IRunStore Runs => _api.GetRequiredService<IRunStore>();

    private IQuota Quota => _api.GetRequiredService<IQuota>();

    private IRunQueue WorkerQueue => _worker.GetRequiredService<IRunQueue>();

    private ControlPlaneActor Actor => new(_tenant.OwnerId, null, null);

    private TenantContext OrgContext => TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId);

    private Task<SubmitResult> SubmitAsync(decimal estimate) =>
        Runs.SubmitAsync(OrgContext, new SubmitRunRequest(
            _tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, "{}", 3, _tenant.OwnerId, null, null, _tenant.OwnerId, estimate),
            TimeSpan.FromHours(24));

    [Fact]
    public async Task Submissions_are_refused_once_they_would_exceed_the_budget()
    {
        await Quota.SetOrganizationBudgetAsync(Actor, _tenant.OrganizationId, 86400, 0.10m);

        Assert.Equal(SubmitStatus.Created, (await SubmitAsync(0.03m)).Status);
        Assert.Equal(SubmitStatus.Created, (await SubmitAsync(0.03m)).Status);
        Assert.Equal(SubmitStatus.Created, (await SubmitAsync(0.03m)).Status); // committed 0.09
        Assert.Equal(SubmitStatus.BudgetExceeded, (await SubmitAsync(0.03m)).Status); // would be 0.12 > 0.10

        var usage = await Quota.GetOrganizationUsageAsync(_tenant.OrganizationId, _tenant.OwnerId);
        Assert.True(usage.HasBudget);
        Assert.Equal(0.10m, usage.LimitUsd);
        Assert.Equal(0.09m, usage.CommittedUsd); // the refused run reserved nothing
    }

    [Fact]
    public async Task A_run_with_no_budget_room_is_not_created()
    {
        await Quota.SetOrganizationBudgetAsync(Actor, _tenant.OrganizationId, 86400, 0.01m);

        var result = await SubmitAsync(0.05m);

        Assert.Equal(SubmitStatus.BudgetExceeded, result.Status);
        Assert.Null(result.Run);
        var queued = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ScalarAsync<long>(c,
            "SELECT count(*) FROM aiep.task_runs WHERE status = 'Queued'"));
        Assert.Equal(0, queued); // nothing was committed
    }

    [Fact]
    public async Task Settling_records_the_actual_cost_against_the_reservation()
    {
        await Quota.SetOrganizationBudgetAsync(Actor, _tenant.OrganizationId, 86400, 100m);
        var sonnet = await CreateDefinitionVersionAsync("fake-sonnet", 2);

        var runId = await _api.GetRequiredService<IRunQueue>().EnqueueAsync(OrgContext,
            new EnqueueRunRequest(_tenant.WorkspaceId, sonnet, "\"hi\"", 3));
        // Enqueue bypasses reservation; add one explicitly so there is something to settle.
        await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.quota_reservations (id, organization_id, workspace_id, run_id, estimated_usd, status)
            VALUES ($1, $2, $3, $4, 0.030000, 'reserved')
            """,
            Guid.CreateVersion7(), _tenant.OrganizationId, _tenant.WorkspaceId, runId));

        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        var executor = new RunExecutor(WorkerQueue, new HandlerRegistry([new CompletionHandler([new FakeModelProvider()])]),
            _worker.GetRequiredService<IPricing>(), TimeProvider.System);
        await executor.ExecuteAsync(claimed!, CancellationToken.None);

        var (status, actual) = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c => (
            await TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.quota_reservations WHERE run_id = $1", runId),
            await TenantDb.ScalarAsync<decimal>(c, "SELECT actual_usd FROM aiep.quota_reservations WHERE run_id = $1", runId)));
        Assert.Equal("settled", status);
        Assert.True(actual > 0m);
    }

    private async Task<Guid> CreateDefinitionVersionAsync(string model, int versionNumber)
    {
        var id = Guid.CreateVersion7();
        await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.task_definition_versions
                (id, organization_id, workspace_id, task_definition_id, version, status,
                 input_schema, output_schema, handler_type, handler_version, model_provider, model_name, published_at, created_by)
            VALUES ($1, $2, $3, $4, $5, 'published',
                    '{}'::jsonb, '{}'::jsonb, 'completion', '1', 'fake', $6, now(), $7)
            """,
            id, _tenant.OrganizationId, _tenant.WorkspaceId, _tenant.TaskDefinitionId, versionNumber, model, _tenant.OwnerId));
        return id;
    }
}
