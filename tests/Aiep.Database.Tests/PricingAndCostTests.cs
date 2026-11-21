using Aiep.Application.Execution;
using Aiep.Application.Execution.Handlers;
using Aiep.Application.Execution.Providers;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aiep.Database.Tests;

/// <summary>
/// Pricing and cost (Phase 4, ADR-0009): the worker computes each run's cost from usage and the versioned pricing row,
/// the two fake profiles differ in cost, and a model without a pricing row has no cost. Isolated migrated database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PricingAndCostTests(PostgresFixture db) : IAsyncLifetime
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
        _tenant = await Seed.TenantAsync(_tdb, "p");
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

    private RunExecutor Executor() => new(
        WorkerQueue, new HandlerRegistry([new CompletionHandler([new FakeModelProvider()])]),
        _worker.GetRequiredService<IPricing>(), TimeProvider.System);

    private async Task<decimal?> RunToCostAsync(Guid versionId)
    {
        var runId = await _api.GetRequiredService<IRunQueue>().EnqueueAsync(
            TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId),
            new EnqueueRunRequest(_tenant.WorkspaceId, versionId, "\"the same input\"", 3));
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        Assert.Equal(runId, claimed!.RunId);
        await Executor().ExecuteAsync(claimed, CancellationToken.None);

        return await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c =>
        {
            var isNull = await TenantDb.ScalarAsync<bool>(c, "SELECT cost_usd IS NULL FROM aiep.task_runs WHERE id = $1", runId);
            return isNull ? (decimal?)null : await TenantDb.ScalarAsync<decimal>(c, "SELECT cost_usd FROM aiep.task_runs WHERE id = $1", runId);
        });
    }

    private async Task<Guid> CreateVersionAsync(string model, int versionNumber)
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

    [Fact]
    public async Task The_costlier_profile_costs_more_for_the_same_input()
    {
        var sonnet = await CreateVersionAsync("fake-sonnet", 2);
        var haiku = await CreateVersionAsync("fake-haiku", 3);

        var sonnetCost = await RunToCostAsync(sonnet);
        var haikuCost = await RunToCostAsync(haiku);

        Assert.NotNull(sonnetCost);
        Assert.NotNull(haikuCost);
        Assert.True(sonnetCost > 0m);
        Assert.True(haikuCost > 0m);
        Assert.True(sonnetCost > haikuCost, $"expected fake-sonnet ({sonnetCost}) to cost more than fake-haiku ({haikuCost})");
    }

    [Fact]
    public async Task A_model_without_a_pricing_row_has_no_cost()
    {
        // The seeded version uses model 'fake-1', which has no pricing row.
        var cost = await RunToCostAsync(_tenant.TaskDefinitionVersionId);

        Assert.Null(cost);
    }

    [Fact]
    public async Task The_cost_equals_tokens_times_the_pricing_row()
    {
        var sonnet = await CreateVersionAsync("fake-sonnet", 2);
        var cost = await RunToCostAsync(sonnet);

        // Recompute from the stored attempt usage and the fake-sonnet pricing (3.00 in / 15.00 out per Mtok).
        var (inTokens, outTokens) = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c => (
            await TenantDb.ScalarAsync<int>(c, "SELECT input_tokens FROM aiep.task_run_attempts WHERE cost_usd IS NOT NULL ORDER BY started_at DESC LIMIT 1"),
            await TenantDb.ScalarAsync<int>(c, "SELECT output_tokens FROM aiep.task_run_attempts WHERE cost_usd IS NOT NULL ORDER BY started_at DESC LIMIT 1")));
        var expected = (inTokens / 1_000_000m * 3.0000m) + (outTokens / 1_000_000m * 15.0000m);

        Assert.Equal(expected, cost);
    }
}
