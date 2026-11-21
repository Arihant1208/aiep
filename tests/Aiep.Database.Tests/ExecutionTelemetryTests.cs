using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aiep.Application.Execution;
using Aiep.Application.Execution.Handlers;
using Aiep.Application.Execution.Providers;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aiep.Database.Tests;

/// <summary>
/// Execution telemetry (ADR-0012): the worker's span links to the run's stored traceparent, and both spans and metrics
/// carry only low-cardinality, payload-free attributes -- never inputs, outputs, tenant ids or run ids.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutionTelemetryTests(PostgresFixture db) : IAsyncLifetime
{
    private const string KnownTraceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    // Everything a span or metric is allowed to say about a run. Anything else is a hygiene failure.
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "aiep.handler.type", "aiep.model.provider", "aiep.model.name", "aiep.attempt.number", "aiep.run.status",
        "handler_type", "model_provider", "model_name", "status",
    };

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
        _tenant = await Seed.TenantAsync(_tdb, "t");
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

    private RunExecutor Executor() =>
        new(WorkerQueue, new HandlerRegistry([new CompletionHandler([new FakeModelProvider()])]), TimeProvider.System);

    private Task<Guid> EnqueueAsync(string? traceparent) =>
        _api.GetRequiredService<IRunQueue>().EnqueueAsync(
            TenantContext.ForOrganization(_tenant.OrganizationId, _tenant.OwnerId),
            new EnqueueRunRequest(_tenant.WorkspaceId, _tenant.TaskDefinitionVersionId, "\"secret-input\"", 3, traceparent));

    [Fact]
    public async Task The_execution_span_links_to_the_submission_trace_and_leaks_no_payload()
    {
        await EnqueueAsync(KnownTraceparent);
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ExecutionTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        await Executor().ExecuteAsync(claimed!, CancellationToken.None);

        var span = Assert.Single(captured);
        Assert.Equal("run.execute", span.OperationName);

        // Linked (not parented) to the submission trace.
        ActivityContext.TryParse(KnownTraceparent, null, out var submitted);
        Assert.Contains(span.Links, link => link.Context.TraceId == submitted.TraceId);

        // Hygiene: only allowlisted, non-sensitive tags; nothing carries the input payload.
        foreach (var tag in span.TagObjects)
        {
            Assert.Contains(tag.Key, AllowedKeys);
            Assert.DoesNotContain("secret-input", tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Equal("Succeeded", span.GetTagItem("aiep.run.status"));
    }

    [Fact]
    public async Task Run_metrics_use_only_low_cardinality_labels()
    {
        await EnqueueAsync(traceparent: null);
        var labelKeys = new HashSet<string>(StringComparer.Ordinal);
        var settledStatuses = new List<string>();

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ExecutionTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                labelKeys.Add(tag.Key);
                if (instrument.Name == "aiep.runs.settled" && tag.Key == "status")
                {
                    settledStatuses.Add(tag.Value?.ToString() ?? string.Empty);
                }
            }
        });
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                labelKeys.Add(tag.Key);
            }
        });
        meterListener.Start();

        var claimed = await WorkerQueue.ClaimAsync("worker-1");
        await Executor().ExecuteAsync(claimed!, CancellationToken.None);
        meterListener.Dispose();

        Assert.Contains("Succeeded", settledStatuses);
        Assert.All(labelKeys, key => Assert.Contains(key, AllowedKeys));
        // Never these.
        Assert.DoesNotContain("organization_id", labelKeys);
        Assert.DoesNotContain("run_id", labelKeys);
        Assert.DoesNotContain("tenant", labelKeys);
    }
}
