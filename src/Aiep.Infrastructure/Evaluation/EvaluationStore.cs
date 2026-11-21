using System.Text.Json;
using Aiep.Application.ControlPlane;
using Aiep.Application.Evaluation;
using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;
using Aiep.Infrastructure.Persistence;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aiep.Infrastructure.Evaluation;

/// <summary>
/// Evaluation datasets and runs over <see cref="TenantDatabase"/> (ADR-0014). Creating a run fans out one task_run per
/// example (the worker executes them like any run); scoring runs the versioned scorers over the completed task_runs and
/// writes per-example results plus the run summary. Scoring happens under the tenant context on demand and is idempotent.
/// </summary>
internal sealed class EvaluationStore(TenantDatabase database, TimeProvider time, IEnumerable<IScorer> scorers)
    : IEvaluationStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly IReadOnlyList<IScorer> _scorers = [.. scorers];

    public Task<EvaluationSuiteView?> CreateSuiteAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string slug, string name, string? description,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            if (await session.Db.EvaluationSuites.AsNoTracking()
                    .AnyAsync(s => s.WorkspaceId == workspaceId && s.Slug == slug, token))
            {
                return (EvaluationSuiteView?)null;
            }

            var suite = new EvaluationSuite
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                Slug = slug,
                Name = name,
                Description = description,
                CreatedBy = actor.SubjectId,
            };
            session.Db.EvaluationSuites.Add(suite);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "evaluation.suite.created", suite.Id));
            await session.Db.SaveChangesAsync(token);
            return ToView(suite);
        }, cancellationToken);

    public Task<IReadOnlyList<EvaluationSuiteView>> ListSuitesAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var suites = await session.Db.EvaluationSuites.AsNoTracking()
                .Where(s => s.WorkspaceId == workspaceId).OrderBy(s => s.Id).ToListAsync(token);
            return (IReadOnlyList<EvaluationSuiteView>)[.. suites.Select(ToView)];
        }, cancellationToken);

    public Task<EvaluationSuiteView?> GetSuiteAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid suiteId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var suite = await session.Db.EvaluationSuites.AsNoTracking()
                .SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Id == suiteId, token);
            return suite is null ? null : ToView(suite);
        }, cancellationToken);

    public Task<EvaluationSuiteVersionView?> CreateSuiteVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid suiteId, string examplesJson,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var suite = await session.Db.EvaluationSuites
                .SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Id == suiteId, token);
            if (suite is null)
            {
                return (EvaluationSuiteVersionView?)null;
            }

            var count = ParseExamples(examplesJson).Count;
            var highest = await session.Db.EvaluationSuiteVersions.AsNoTracking()
                .Where(v => v.SuiteId == suiteId).MaxAsync(v => (int?)v.Version, token) ?? 0;

            var version = new EvaluationSuiteVersion
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                SuiteId = suiteId,
                Version = highest + 1,
                Status = "draft",
                Examples = examplesJson,
                ExampleCount = count,
                CreatedBy = actor.SubjectId,
            };
            session.Db.EvaluationSuiteVersions.Add(version);
            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "evaluation.suite.version.created", version.Id));
            await session.Db.SaveChangesAsync(token);
            return ToView(version);
        }, cancellationToken);

    public Task<EvaluationSuiteVersionView?> PublishSuiteVersionAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid suiteId, int version,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var row = await session.Db.EvaluationSuiteVersions
                .SingleOrDefaultAsync(v => v.SuiteId == suiteId && v.Version == version, token);
            if (row is null)
            {
                return (EvaluationSuiteVersionView?)null;
            }

            if (row.Status != "published")
            {
                var now = time.GetUtcNow();
                row.Status = "published";
                row.PublishedAt = now;
                var suite = await session.Db.EvaluationSuites.SingleAsync(s => s.Id == suiteId, token);
                suite.CurrentVersion = version;
                suite.UpdatedAt = now;
                session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "evaluation.suite.version.published", row.Id));
                await session.Db.SaveChangesAsync(token);
            }

            return ToView(row);
        }, cancellationToken);

    public Task<EvaluationSuiteVersionView?> GetSuiteVersionAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid suiteId, int version, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var row = await session.Db.EvaluationSuiteVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.WorkspaceId == workspaceId && v.SuiteId == suiteId && v.Version == version, token);
            return row is null ? null : ToView(row);
        }, cancellationToken);

    public Task<IReadOnlyList<EvaluationResultRow>?> ListResultsAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid evaluationRunId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var run = await session.Db.EvaluationRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == evaluationRunId, token);
            if (run is null)
            {
                return (IReadOnlyList<EvaluationResultRow>?)null;
            }

            var results = await session.Db.EvaluationResults.AsNoTracking()
                .Where(r => r.EvaluationRunId == evaluationRunId).OrderBy(r => r.ExampleOrdinal).ToListAsync(token);
            var runIds = results.Select(r => r.TaskRunId).ToList();
            var taskRuns = await session.Db.TaskRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, token);

            var rows = results.Select(r =>
            {
                var taskRun = taskRuns[r.TaskRunId];
                double? latency = taskRun.StartedAt is { } s && taskRun.CompletedAt is { } c ? (c - s).TotalMilliseconds : null;
                return new EvaluationResultRow(r.ExampleOrdinal, taskRun.Status, r.Passed, r.Score, latency, taskRun.CostUsd);
            }).ToList();
            return (IReadOnlyList<EvaluationResultRow>?)rows;
        }, cancellationToken);

    public Task<EvaluationRunView?> CreateEvaluationRunAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid suiteVersionId, Guid taskDefinitionVersionId,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var suiteVersion = await session.Db.EvaluationSuiteVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.WorkspaceId == workspaceId && v.Id == suiteVersionId, token);
            var defVersion = await session.Db.TaskDefinitionVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.WorkspaceId == workspaceId && v.Id == taskDefinitionVersionId, token);
            if (suiteVersion is null || suiteVersion.Status != "published"
                || defVersion is null || defVersion.Status != "published")
            {
                return (EvaluationRunView?)null;
            }

            var examples = ParseExamples(suiteVersion.Examples);
            var now = time.GetUtcNow();
            var evaluationRun = new EvaluationRun
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                SuiteVersionId = suiteVersionId,
                TaskDefinitionVersionId = taskDefinitionVersionId,
                Status = "Running",
                ExampleCount = examples.Count,
                StartedAt = now,
                CreatedBy = actor.SubjectId,
            };
            session.Db.EvaluationRuns.Add(evaluationRun);

            var taskRunIds = new Guid[examples.Count];
            for (var i = 0; i < examples.Count; i++)
            {
                var taskRun = new TaskRun
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    WorkspaceId = workspaceId,
                    TaskDefinitionVersionId = taskDefinitionVersionId,
                    Status = "Queued",
                    MaxAttempts = defVersion.MaxAttempts,
                    Input = examples[i].Input,
                    NextAttemptAt = now,
                    CreatedBy = actor.SubjectId,
                };
                session.Db.TaskRuns.Add(taskRun);
                taskRunIds[i] = taskRun.Id;
            }

            // Persist the run and its task_runs before the results, whose composite FKs reference both (EF does not know
            // these DB-only foreign keys, so it cannot order the inserts itself).
            await session.Db.SaveChangesAsync(token);

            for (var i = 0; i < examples.Count; i++)
            {
                session.Db.EvaluationResults.Add(new EvaluationResult
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    WorkspaceId = workspaceId,
                    EvaluationRunId = evaluationRun.Id,
                    ExampleOrdinal = i,
                    TaskRunId = taskRunIds[i],
                });
            }

            session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "evaluation.run.created", evaluationRun.Id));
            await session.Db.SaveChangesAsync(token);

            if (examples.Count > 0)
            {
                await using var notify = new NpgsqlCommand("SELECT pg_notify('aiep_runs', '')", session.Connection, session.Transaction);
                await notify.ExecuteScalarAsync(token);
            }

            return ToView(evaluationRun);
        }, cancellationToken);

    public Task<EvaluationRunView?> ScoreAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid evaluationRunId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var evaluationRun = await session.Db.EvaluationRuns
                .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == evaluationRunId, token);
            if (evaluationRun is null)
            {
                return (EvaluationRunView?)null;
            }

            if (evaluationRun.Status == "Completed")
            {
                return ToView(evaluationRun);
            }

            var results = await session.Db.EvaluationResults
                .Where(r => r.EvaluationRunId == evaluationRunId).OrderBy(r => r.ExampleOrdinal).ToListAsync(token);
            var runIds = results.Select(r => r.TaskRunId).ToList();
            var taskRuns = await session.Db.TaskRuns
                .Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, token);

            if (results.Any(r => !IsTerminal(taskRuns[r.TaskRunId].Status)))
            {
                return ToView(evaluationRun); // still running; not scored yet
            }

            var suiteVersion = await session.Db.EvaluationSuiteVersions.AsNoTracking()
                .SingleAsync(v => v.Id == evaluationRun.SuiteVersionId, token);
            var examples = ParseExamples(suiteVersion.Examples);
            var now = time.GetUtcNow();

            var latencies = new List<double>();
            decimal totalCost = 0m;
            var succeeded = 0;
            var matched = 0;

            foreach (var result in results)
            {
                var taskRun = taskRuns[result.TaskRunId];
                var expected = result.ExampleOrdinal < examples.Count ? examples[result.ExampleOrdinal].Expected : "null";

                if (taskRun.Status == "Succeeded" && taskRun.Output is { } output)
                {
                    succeeded++;
                    var scoreValues = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    var passed = true;
                    foreach (var scorer in _scorers)
                    {
                        var outcome = scorer.Score(output, expected);
                        scoreValues[scorer.Name] = outcome.Score;
                        if (scorer.Name == "exact_match")
                        {
                            passed = outcome.Passed;
                        }
                    }

                    if (passed)
                    {
                        matched++;
                    }

                    result.Score = JsonSerializer.Serialize(scoreValues, Json);
                    result.Passed = passed;
                }
                else
                {
                    result.Passed = false;
                }

                if (taskRun.StartedAt is { } started && taskRun.CompletedAt is { } completed)
                {
                    latencies.Add((completed - started).TotalMilliseconds);
                }

                totalCost += taskRun.CostUsd ?? 0m;
                result.ScoredAt = now;
            }

            var count = results.Count;
            var summary = new
            {
                exampleCount = count,
                succeeded,
                exactMatchRate = count == 0 ? 0m : Math.Round((decimal)matched / count, 4),
                avgLatencyMs = latencies.Count == 0 ? 0d : Math.Round(latencies.Average(), 2),
                p95LatencyMs = Percentile(latencies, 0.95),
                totalCostUsd = totalCost,
                avgCostUsd = count == 0 ? 0m : Math.Round(totalCost / count, 6),
            };

            evaluationRun.Status = "Completed";
            evaluationRun.Summary = JsonSerializer.Serialize(summary, Json);
            evaluationRun.CompletedAt = now;
            evaluationRun.UpdatedAt = now;
            await session.Db.SaveChangesAsync(token);
            return ToView(evaluationRun);
        }, cancellationToken);

    public Task<EvaluationRunView?> GetEvaluationRunAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid evaluationRunId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var run = await session.Db.EvaluationRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == evaluationRunId, token);
            return run is null ? null : ToView(run);
        }, cancellationToken);

    private static bool IsTerminal(string status) =>
        Enum.TryParse<TaskRunStatus>(status, out var parsed) && TaskRunTransitions.IsTerminal(parsed);

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Count - 1)], 2);
    }

    private static List<(string Input, string Expected)> ParseExamples(string examplesJson)
    {
        var list = new List<(string, string)>();
        using var document = JsonDocument.Parse(examplesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var input = element.TryGetProperty("input", out var inputElement) ? inputElement.GetRawText() : "null";
            var expected = element.TryGetProperty("expected", out var expectedElement) ? expectedElement.GetRawText() : "null";
            list.Add((input, expected));
        }

        return list;
    }

    private static EvaluationSuiteView ToView(EvaluationSuite s) => new(
        s.Id, s.WorkspaceId, s.Slug, s.Name, s.Description, s.Status, s.CurrentVersion, s.CreatedAt);

    private static EvaluationSuiteVersionView ToView(EvaluationSuiteVersion v) => new(
        v.Id, v.SuiteId, v.Version, v.Status, v.ExampleCount, v.PublishedAt, v.CreatedAt);

    private static EvaluationRunView ToView(EvaluationRun r) => new(
        r.Id, r.SuiteVersionId, r.TaskDefinitionVersionId, r.Status, r.ExampleCount, r.Summary, r.CreatedAt, r.CompletedAt);

    private static AuditEvent Audit(ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string action, Guid targetId) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        WorkspaceId = workspaceId,
        ActorType = actor.ActorType,
        ActorSubjectId = actor.SubjectId,
        ActorApiKeyId = actor.ApiKeyId,
        Action = action,
        TargetType = "evaluation",
        TargetId = targetId,
        Outcome = "success",
        CorrelationId = actor.CorrelationId,
    };
}
