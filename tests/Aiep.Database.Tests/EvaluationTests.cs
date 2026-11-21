using System.Text.Json;
using Aiep.Application.ControlPlane;
using Aiep.Application.Evaluation;
using Aiep.Application.Execution;
using Aiep.Application.Execution.Handlers;
using Aiep.Application.Execution.Providers;
using Aiep.Domain.Runs;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aiep.Database.Tests;

/// <summary>
/// The evaluation engine (Phase 4, ADR-0014): a dataset version fans out into one task_run per example, the worker runs
/// them, and scoring writes per-example results and a summary (exact-match rate, latency, cost). Isolated migrated database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EvaluationTests(PostgresFixture db) : IAsyncLifetime
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
        _tenant = await Seed.TenantAsync(_tdb, "e");
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

    private IEvaluationStore Evaluations => _api.GetRequiredService<IEvaluationStore>();

    private IRunQueue WorkerQueue => _worker.GetRequiredService<IRunQueue>();

    private ControlPlaneActor Actor => new(_tenant.OwnerId, null, null);

    // Echoes the input as output, so exact-match reflects whether input equals the example's expected value.
    private RunExecutor Executor() => new(
        WorkerQueue,
        new HandlerRegistry([new EchoHandler()]),
        _worker.GetRequiredService<IPricing>(),
        TimeProvider.System);

    // The real completion handler over the fake provider, so the two model profiles produce different tokens and cost.
    private RunExecutor RealExecutor() => new(
        WorkerQueue,
        new HandlerRegistry([new CompletionHandler([new FakeModelProvider()])]),
        _worker.GetRequiredService<IPricing>(),
        TimeProvider.System);

    private async Task ExecuteAllQueuedAsync(RunExecutor? executor = null)
    {
        executor ??= Executor();
        while (await WorkerQueue.ClaimAsync("worker-1") is { } claimed)
        {
            await executor.ExecuteAsync(claimed, CancellationToken.None);
        }
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

    private static decimal TotalCost(string summaryJson)
    {
        using var summary = JsonDocument.Parse(summaryJson);
        return summary.RootElement.GetProperty("totalCostUsd").GetDecimal();
    }

    private async Task<Guid> PublishedSuiteVersionAsync(string examplesJson)
    {
        var suite = (await Evaluations.CreateSuiteAsync(Actor, _tenant.OrganizationId, _tenant.WorkspaceId, "set", "Set", null))!;
        var version = (await Evaluations.CreateSuiteVersionAsync(Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suite.Id, examplesJson))!;
        await Evaluations.PublishSuiteVersionAsync(Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suite.Id, version.Version);
        return version.Id;
    }

    [Fact]
    public async Task A_run_fans_out_executes_scores_and_summarizes()
    {
        // Two examples: the first matches (output == expected), the second does not -> 50% exact-match.
        const string Examples = """[{"input":{"a":1},"expected":{"a":1}},{"input":{"a":2},"expected":{"a":1}}]""";
        var suiteVersion = await PublishedSuiteVersionAsync(Examples);

        var run = (await Evaluations.CreateEvaluationRunAsync(
            Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suiteVersion, _tenant.TaskDefinitionVersionId))!;
        Assert.Equal(2, run.ExampleCount);
        Assert.Equal("Running", run.Status);

        await ExecuteAllQueuedAsync();

        var scored = (await Evaluations.ScoreAsync(_tenant.OrganizationId, _tenant.OwnerId, _tenant.WorkspaceId, run.Id))!;
        Assert.Equal("Completed", scored.Status);

        using var summary = JsonDocument.Parse(scored.Summary!);
        var root = summary.RootElement;
        Assert.Equal(2, root.GetProperty("exampleCount").GetInt32());
        Assert.Equal(2, root.GetProperty("succeeded").GetInt32());
        Assert.Equal(0.5m, root.GetProperty("exactMatchRate").GetDecimal());
    }

    [Fact]
    public async Task Two_configurations_on_one_dataset_differ_in_cost()
    {
        // One dataset, two configurations (a costlier and a cheaper model profile) -- the Phase 4 headline.
        var suiteVersion = await PublishedSuiteVersionAsync(
            """[{"input":{"q":"a"},"expected":{"a":1}},{"input":{"q":"b"},"expected":{"a":2}}]""");
        var sonnet = await CreateDefinitionVersionAsync("fake-sonnet", 2);
        var haiku = await CreateDefinitionVersionAsync("fake-haiku", 3);

        var runSonnet = (await Evaluations.CreateEvaluationRunAsync(Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suiteVersion, sonnet))!;
        var runHaiku = (await Evaluations.CreateEvaluationRunAsync(Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suiteVersion, haiku))!;
        await ExecuteAllQueuedAsync(RealExecutor());

        var scoredSonnet = (await Evaluations.ScoreAsync(_tenant.OrganizationId, _tenant.OwnerId, _tenant.WorkspaceId, runSonnet.Id))!;
        var scoredHaiku = (await Evaluations.ScoreAsync(_tenant.OrganizationId, _tenant.OwnerId, _tenant.WorkspaceId, runHaiku.Id))!;

        Assert.Equal("Completed", scoredSonnet.Status);
        Assert.Equal("Completed", scoredHaiku.Status);
        Assert.True(TotalCost(scoredSonnet.Summary!) > TotalCost(scoredHaiku.Summary!),
            $"expected fake-sonnet to cost more: {scoredSonnet.Summary} vs {scoredHaiku.Summary}");
    }

    [Fact]
    public async Task Scoring_waits_until_all_task_runs_finish()
    {
        var suiteVersion = await PublishedSuiteVersionAsync("""[{"input":{"a":1},"expected":{"a":1}}]""");
        var run = (await Evaluations.CreateEvaluationRunAsync(
            Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suiteVersion, _tenant.TaskDefinitionVersionId))!;

        // No task_runs executed yet.
        var scored = (await Evaluations.ScoreAsync(_tenant.OrganizationId, _tenant.OwnerId, _tenant.WorkspaceId, run.Id))!;

        Assert.Equal("Running", scored.Status);
        Assert.Null(scored.Summary);
    }

    [Fact]
    public async Task A_run_against_an_unpublished_suite_version_is_rejected()
    {
        var suite = (await Evaluations.CreateSuiteAsync(Actor, _tenant.OrganizationId, _tenant.WorkspaceId, "draft", "Draft", null))!;
        var version = (await Evaluations.CreateSuiteVersionAsync(
            Actor, _tenant.OrganizationId, _tenant.WorkspaceId, suite.Id, "[]"))!;

        var run = await Evaluations.CreateEvaluationRunAsync(
            Actor, _tenant.OrganizationId, _tenant.WorkspaceId, version.Id, _tenant.TaskDefinitionVersionId);

        Assert.Null(run);
    }

    private sealed class EchoHandler : ITaskHandler
    {
        public string HandlerType => "completion";

        public string Version => "1";

        public Task<HandlerResult> ExecuteAsync(HandlerInput input, CancellationToken cancellationToken) =>
            Task.FromResult(new HandlerResult(input.InputJson, new ModelUsage(1, 1, TimeSpan.FromMilliseconds(3))));
    }
}
