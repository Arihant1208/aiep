using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Evaluation;
using Aiep.Application.Execution;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary>
/// Evaluation datasets, runs, comparison and export (ADR-0014, ADR-0015), workspace-scoped under
/// <c>/v1/organizations/{organizationId}/workspaces/{workspaceId}</c>. Permissions are <c>evals:read</c>/<c>evals:write</c>.
/// </summary>
public static partial class EvaluationEndpoints
{
    public static RouteGroupBuilder MapEvaluationEndpoints(this RouteGroupBuilder v1)
    {
        const string Suites = "/organizations/{organizationId}/workspaces/{workspaceId}/evaluation-suites";
        const string Runs = "/organizations/{organizationId}/workspaces/{workspaceId}/evaluation-runs";

        v1.MapPost(Suites, async (
            string organizationId, string workspaceId, CreateEvaluationSuiteRequest request, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsWrite, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            var slug = request.Slug?.Trim();
            var name = request.Name?.Trim();
            if (slug is null || !SlugPattern().IsMatch(slug))
            {
                return Problems.BadRequest("slug must be 1-63 characters, lowercase letters, digits and hyphens.");
            }

            if (string.IsNullOrEmpty(name) || name.Length > 200)
            {
                return Problems.BadRequest("name is required and must be 1-200 characters.");
            }

            var created = await evaluations.CreateSuiteAsync(
                EndpointAuth.Actor(http), scope.Organization, scope.Workspace, slug, name, request.Description?.Trim(), ct);
            return created is null
                ? Problems.Conflict("evaluation.slug_taken", $"An evaluation suite with slug '{slug}' already exists.")
                : Results.Created($"/v1{Suites[1..].Replace("{organizationId}", organizationId).Replace("{workspaceId}", workspaceId)}/{EvaluationSuiteResponse.From(created).Id}", EvaluationSuiteResponse.From(created));
        });

        v1.MapGet(Suites, async (
            string organizationId, string workspaceId, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsRead, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            var suites = await evaluations.ListSuitesAsync(scope.Organization, scope.Subject!.SubjectId, scope.Workspace, ct);
            return Results.Ok(suites.Select(EvaluationSuiteResponse.From).ToList());
        });

        v1.MapPost($"{Suites}/{{suiteId}}/versions", async (
            string organizationId, string workspaceId, string suiteId, CreateEvaluationVersionRequest request, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsWrite, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            if (!ResourceId.TryParse(ResourceId.EvaluationSuite, suiteId, out var suite))
            {
                return Problems.NotFound();
            }

            if (request.Examples is not { ValueKind: JsonValueKind.Array } examples)
            {
                return Problems.BadRequest("examples must be a JSON array of {input, expected} objects.");
            }

            var created = await evaluations.CreateSuiteVersionAsync(
                EndpointAuth.Actor(http), scope.Organization, scope.Workspace, suite, examples.GetRawText(), ct);
            return created is null ? Problems.NotFound() : Results.Ok(EvaluationSuiteVersionResponse.From(created));
        });

        v1.MapPost($"{Suites}/{{suiteId}}/versions/{{version:int}}/publish", async (
            string organizationId, string workspaceId, string suiteId, int version, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsWrite, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            if (!ResourceId.TryParse(ResourceId.EvaluationSuite, suiteId, out var suite))
            {
                return Problems.NotFound();
            }

            var published = await evaluations.PublishSuiteVersionAsync(
                EndpointAuth.Actor(http), scope.Organization, scope.Workspace, suite, version, ct);
            return published is null ? Problems.NotFound() : Results.Ok(EvaluationSuiteVersionResponse.From(published));
        });

        v1.MapPost(Runs, async (
            string organizationId, string workspaceId, CreateEvaluationRunRequest request, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsWrite, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            if (!ResourceId.TryParse(ResourceId.EvaluationSuite, request.EvaluationSuiteId, out var suiteId)
                || !ResourceId.TryParse(ResourceId.TaskDefinition, request.TaskDefinitionId, out var definitionId))
            {
                return Problems.NotFound();
            }

            // Resolve the dataset version and the configuration version by number (current published if omitted).
            var suite = await evaluations.GetSuiteAsync(scope.Organization, scope.Subject!.SubjectId, scope.Workspace, suiteId, ct);
            var suiteVersionNumber = request.SuiteVersion ?? suite?.CurrentVersion;
            var definition = await catalog.GetDefinitionAsync(scope.Organization, scope.Subject.SubjectId, scope.Workspace, definitionId, ct);
            var definitionVersionNumber = request.TaskDefinitionVersion ?? definition?.CurrentVersion;
            if (suite is null || definition is null || suiteVersionNumber is null || definitionVersionNumber is null)
            {
                return Problems.Conflict("evaluation.unresolved_version", "The dataset or configuration has no published version to run.");
            }

            var suiteVersion = await evaluations.GetSuiteVersionAsync(scope.Organization, scope.Subject.SubjectId, scope.Workspace, suiteId, suiteVersionNumber.Value, ct);
            var definitionVersion = await catalog.GetVersionAsync(scope.Organization, scope.Subject.SubjectId, scope.Workspace, definitionId, definitionVersionNumber.Value, ct);
            if (suiteVersion is null || definitionVersion is null)
            {
                return Problems.NotFound();
            }

            var run = await evaluations.CreateEvaluationRunAsync(
                EndpointAuth.Actor(http), scope.Organization, scope.Workspace, suiteVersion.Id, definitionVersion.Id, ct);
            return run is null
                ? Problems.Conflict("evaluation.not_runnable", "The dataset version or configuration version is not published.")
                : Results.Accepted($"/v1{Runs[1..].Replace("{organizationId}", organizationId).Replace("{workspaceId}", workspaceId)}/{EvaluationRunResponse.From(run).Id}", EvaluationRunResponse.From(run));
        });

        v1.MapGet($"{Runs}/{{runId}}", async (
            string organizationId, string workspaceId, string runId, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsRead, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            if (!ResourceId.TryParse(ResourceId.EvaluationRun, runId, out var run))
            {
                return Problems.NotFound();
            }

            // Score on read so the summary is available once all task_runs have finished.
            var view = await evaluations.ScoreAsync(scope.Organization, scope.Subject!.SubjectId, scope.Workspace, run, ct);
            return view is null ? Problems.NotFound() : Results.Ok(EvaluationRunResponse.From(view));
        });

        v1.MapGet($"{Runs}/{{runId}}/results", async (
            string organizationId, string workspaceId, string runId, string? format, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsRead, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            if (!ResourceId.TryParse(ResourceId.EvaluationRun, runId, out var run))
            {
                return Problems.NotFound();
            }

            await evaluations.ScoreAsync(scope.Organization, scope.Subject!.SubjectId, scope.Workspace, run, ct);
            var rows = await evaluations.ListResultsAsync(scope.Organization, scope.Subject.SubjectId, scope.Workspace, run, ct);
            if (rows is null)
            {
                return Problems.NotFound();
            }

            return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(ToCsv(rows), "text/csv")
                : Results.Ok(rows.Select(EvaluationResultRowResponse.From).ToList());
        });

        v1.MapGet($"{Runs}/compare", async (
            string organizationId, string workspaceId, string a, string b, HttpContext http,
            IControlPlaneStore controlPlane, IEvaluationStore evaluations, CancellationToken ct) =>
        {
            var scope = await WorkspaceAsync(organizationId, workspaceId, Permission.EvalsRead, http, controlPlane, ct);
            if (scope.Failure is not null)
            {
                return scope.Failure;
            }

            if (!ResourceId.TryParse(ResourceId.EvaluationRun, a, out var runA)
                || !ResourceId.TryParse(ResourceId.EvaluationRun, b, out var runB))
            {
                return Problems.NotFound();
            }

            var viewA = await evaluations.ScoreAsync(scope.Organization, scope.Subject!.SubjectId, scope.Workspace, runA, ct);
            var viewB = await evaluations.ScoreAsync(scope.Organization, scope.Subject.SubjectId, scope.Workspace, runB, ct);
            if (viewA is null || viewB is null)
            {
                return Problems.NotFound();
            }

            if (viewA.SuiteVersionId != viewB.SuiteVersionId)
            {
                return Problems.BadRequest("Both runs must evaluate the same dataset version to be compared.", "evaluation.different_datasets");
            }

            return Results.Ok(new EvaluationComparisonResponse(
                EvaluationRunResponse.From(viewA), EvaluationRunResponse.From(viewB)));
        });

        return v1;
    }

    private static async Task<(AuthorizationSubject? Subject, Guid Organization, Guid Workspace, IResult? Failure)> WorkspaceAsync(
        string organizationId, string workspaceId, string permission, HttpContext http, IControlPlaneStore store, CancellationToken ct)
    {
        var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, store, ct);
        if (failure is not null)
        {
            return (null, default, default, failure);
        }

        return EndpointAuth.CheckWorkspace(subject!, ws, permission) is { } denied
            ? (null, default, default, denied)
            : (subject, org, ws, null);
    }

    private static string ToCsv(IReadOnlyList<EvaluationResultRow> rows)
    {
        var builder = new StringBuilder("example_ordinal,task_run_status,passed,latency_ms,cost_usd\n");
        foreach (var row in rows)
        {
            builder.Append(row.ExampleOrdinal).Append(',')
                .Append(row.TaskRunStatus).Append(',')
                .Append(row.Passed?.ToString() ?? string.Empty).Append(',')
                .Append(row.LatencyMs?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(row.CostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('\n');
        }

        return builder.ToString();
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();
}

public sealed record CreateEvaluationSuiteRequest(string? Slug, string? Name, string? Description);

public sealed record CreateEvaluationVersionRequest(JsonElement? Examples);

public sealed record CreateEvaluationRunRequest(
    string? EvaluationSuiteId, int? SuiteVersion, string? TaskDefinitionId, int? TaskDefinitionVersion);

public sealed record EvaluationSuiteResponse(
    string Id, string Slug, string Name, string? Description, string Status, int? CurrentVersion, DateTimeOffset CreatedAt)
{
    public static EvaluationSuiteResponse From(EvaluationSuiteView view) => new(
        ResourceId.Format(ResourceId.EvaluationSuite, view.Id),
        view.Slug, view.Name, view.Description, view.Status, view.CurrentVersion, view.CreatedAt);
}

public sealed record EvaluationSuiteVersionResponse(string SuiteId, int Version, string Status, int ExampleCount, DateTimeOffset? PublishedAt)
{
    public static EvaluationSuiteVersionResponse From(EvaluationSuiteVersionView view) => new(
        ResourceId.Format(ResourceId.EvaluationSuite, view.SuiteId), view.Version, view.Status, view.ExampleCount, view.PublishedAt);
}

public sealed record EvaluationRunResponse(
    string Id, string Status, int ExampleCount, JsonElement? Summary, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static EvaluationRunResponse From(EvaluationRunView view) => new(
        ResourceId.Format(ResourceId.EvaluationRun, view.Id),
        view.Status,
        view.ExampleCount,
        view.Summary is null ? null : JsonSerializer.Deserialize<JsonElement>(view.Summary),
        view.CreatedAt,
        view.CompletedAt);
}

public sealed record EvaluationResultRowResponse(
    int ExampleOrdinal, string TaskRunStatus, bool? Passed, JsonElement? Score, double? LatencyMs, decimal? CostUsd)
{
    public static EvaluationResultRowResponse From(EvaluationResultRow row) => new(
        row.ExampleOrdinal, row.TaskRunStatus, row.Passed,
        row.Score is null ? null : JsonSerializer.Deserialize<JsonElement>(row.Score), row.LatencyMs, row.CostUsd);
}

public sealed record EvaluationComparisonResponse(EvaluationRunResponse A, EvaluationRunResponse B);
