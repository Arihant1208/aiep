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
/// The worker's execution path (<see cref="RunExecutor"/>) end to end against the real queue and the fake provider:
/// success, permanent failure, retry, dead-letter, a missing handler and cooperative cancellation. Each test runs in its
/// own migrated database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunExecutorTests(PostgresFixture db) : IAsyncLifetime
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
        _tenant = await Seed.TenantAsync(_tdb, "x");
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

    private RunExecutor Executor(IHandlerRegistry registry) =>
        new(WorkerQueue, registry, TimeProvider.System);

    private Task<Guid> EnqueueAsync(Guid versionId, int maxAttempts = 3) =>
        _api.GetRequiredService<IRunQueue>().EnqueueAsync(
            TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId),
            new EnqueueRunRequest(_tenant.WorkspaceId, versionId, "\"hi\"", maxAttempts));

    private Task<string> RunStatusAsync(Guid runId) =>
        _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId,
            c => TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId));

    private async Task<Guid> CreatePublishedVersionAsync(int leaseSeconds, int versionNumber)
    {
        var id = Guid.CreateVersion7();
        await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.task_definition_versions
                (id, organization_id, workspace_id, task_definition_id, version, status,
                 input_schema, output_schema, handler_type, handler_version, model_provider, model_name,
                 lease_seconds, published_at, created_by)
            VALUES ($1, $2, $3, $4, $5, 'published',
                    '{}'::jsonb, '{}'::jsonb, 'completion', '1', 'fake', 'fake-1', $6, now(), $7)
            """,
            id, _tenant.OrganizationId, _tenant.WorkspaceId, _tenant.TaskDefinitionId, versionNumber,
            leaseSeconds, _tenant.OwnerId));
        return id;
    }

    private static IHandlerRegistry RealRegistry() =>
        new HandlerRegistry([new CompletionHandler([new FakeModelProvider()])]);

    private static IHandlerRegistry RegistryOf(ITaskHandler handler) => new HandlerRegistry([handler]);

    // ---- outcomes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_run_persists_output_and_usage()
    {
        var runId = await EnqueueAsync(_tenant.TaskDefinitionVersionId);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");

        var outcome = await Executor(RealRegistry()).ExecuteAsync(claimed!, CancellationToken.None);

        Assert.Equal(TaskRunStatus.Succeeded, outcome);
        var (status, output, tokens) = await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, async c => (
            await TenantDb.ScalarAsync<string>(c, "SELECT status FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<string>(c, "SELECT output::text FROM aiep.task_runs WHERE id = $1", runId),
            await TenantDb.ScalarAsync<int>(c,
                "SELECT output_tokens FROM aiep.task_run_attempts WHERE id = $1", claimed!.AttemptId)));
        Assert.Equal("Succeeded", status);
        Assert.Contains("echo:fake-1", output);
        Assert.True(tokens > 0);
    }

    [Fact]
    public async Task A_permanent_handler_error_fails_the_run_without_retrying()
    {
        await EnqueueAsync(_tenant.TaskDefinitionVersionId);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        var handler = new DelegatingHandler((_, _) => throw new HandlerException(ErrorCategory.Validation, "bad input"));

        var outcome = await Executor(RegistryOf(handler)).ExecuteAsync(claimed!, CancellationToken.None);

        Assert.Equal(TaskRunStatus.Failed, outcome);
        Assert.Equal("Failed", await RunStatusAsync(claimed!.RunId));
    }

    [Fact]
    public async Task A_retryable_error_with_attempts_left_returns_the_run_to_the_queue()
    {
        await EnqueueAsync(_tenant.TaskDefinitionVersionId, maxAttempts: 3);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        var handler = new DelegatingHandler((_, _) => throw new ModelProviderException(ErrorCategory.Transient, "503"));

        var outcome = await Executor(RegistryOf(handler)).ExecuteAsync(claimed!, CancellationToken.None);

        Assert.Equal(TaskRunStatus.RetryWait, outcome);
        Assert.Equal("RetryWait", await RunStatusAsync(claimed!.RunId));
    }

    [Fact]
    public async Task A_retryable_error_with_no_attempts_left_dead_letters_the_run()
    {
        await EnqueueAsync(_tenant.TaskDefinitionVersionId, maxAttempts: 1);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        var handler = new DelegatingHandler((_, _) => throw new ModelProviderException(ErrorCategory.RateLimited, "429"));

        var outcome = await Executor(RegistryOf(handler)).ExecuteAsync(claimed!, CancellationToken.None);

        Assert.Equal(TaskRunStatus.DeadLettered, outcome);
        Assert.Equal("DeadLettered", await RunStatusAsync(claimed!.RunId));
    }

    [Fact]
    public async Task A_run_whose_handler_is_not_allowlisted_fails()
    {
        await EnqueueAsync(_tenant.TaskDefinitionVersionId);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");

        var outcome = await Executor(new HandlerRegistry([])).ExecuteAsync(claimed!, CancellationToken.None);

        Assert.Equal(TaskRunStatus.Failed, outcome);
        Assert.Equal("Failed", await RunStatusAsync(claimed!.RunId));
    }

    [Fact]
    public async Task A_requested_cancellation_stops_the_handler_and_cancels_the_run()
    {
        // A short lease so the heartbeat observes the cancellation quickly.
        var version = await CreatePublishedVersionAsync(leaseSeconds: 5, versionNumber: 2);
        var runId = await EnqueueAsync(version);
        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        var started = new TaskCompletionSource();
        var handler = new DelegatingHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HandlerResult("{}", new ModelUsage(0, 0, TimeSpan.Zero));
        });

        var execute = Executor(RegistryOf(handler)).ExecuteAsync(claimed!, CancellationToken.None);
        await started.Task;
        await _tdb.RunAsync("aiep_api", _tenant.OrganizationId, _tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            "UPDATE aiep.task_runs SET cancel_requested_at = now() WHERE id = $1", runId));

        var outcome = await execute;

        Assert.Equal(TaskRunStatus.Cancelled, outcome);
        Assert.Equal("Cancelled", await RunStatusAsync(runId));
    }

    private sealed class DelegatingHandler(Func<HandlerInput, CancellationToken, Task<HandlerResult>> behavior)
        : ITaskHandler
    {
        public string HandlerType => "completion";

        public string Version => "1";

        public Task<HandlerResult> ExecuteAsync(HandlerInput input, CancellationToken cancellationToken) =>
            behavior(input, cancellationToken);
    }
}
