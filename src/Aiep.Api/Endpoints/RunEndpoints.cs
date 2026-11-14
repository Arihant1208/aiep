using System.Diagnostics;
using System.Text.Json;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Ids;
using Aiep.Application.Tenancy;

namespace Aiep.Api.Endpoints;

/// <summary>
/// Task runs (ADR-0005, ADR-0015), workspace-scoped under
/// <c>/v1/organizations/{organizationId}/workspaces/{workspaceId}/runs</c>. Submit is idempotent via the
/// <c>Idempotency-Key</c> header; the result (<c>output</c>) is only returned to callers with the result-read permission.
/// </summary>
public static class RunEndpoints
{
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private const int MaxListLimit = 100;

    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder v1)
    {
        const string Base = "/organizations/{organizationId}/workspaces/{workspaceId}/runs";

        v1.MapPost(Base, async (
            string organizationId, string workspaceId, SubmitRunBody body, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, IRunStore runs, CancellationToken ct) =>
        {
            var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(subject!, ws, Permission.RunsSubmit) is { } denied)
            {
                return denied;
            }

            if (!ResourceId.TryParse(ResourceId.TaskDefinition, body.TaskDefinitionId, out var definitionId))
            {
                return Problems.NotFound();
            }

            if (body.Input is not { } input || input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return Problems.BadRequest("input is required.");
            }

            var definition = await catalog.GetDefinitionAsync(org, subject!.SubjectId, ws, definitionId, ct);
            if (definition is null)
            {
                return Problems.NotFound();
            }

            var versionNumber = body.Version ?? definition.CurrentVersion;
            if (versionNumber is null)
            {
                return Problems.Conflict("definition.no_published_version", "The task definition has no published version.");
            }

            var version = await catalog.GetVersionAsync(org, subject.SubjectId, ws, definitionId, versionNumber.Value, ct);
            if (version is null)
            {
                return Problems.NotFound();
            }

            if (version.Status != "published")
            {
                return Problems.Conflict("definition.version_not_published", "That version is not published.");
            }

            var actor = EndpointAuth.Actor(http);
            var callerId = actor.ApiKeyId ?? actor.SubjectId;
            var idempotencyKey = http.Request.Headers.TryGetValue("Idempotency-Key", out var header)
                ? header.ToString()
                : null;
            if (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Problems.BadRequest("Idempotency-Key must not be blank.");
            }

            var request = new SubmitRunRequest(
                ws, version.Id, input.GetRawText(), version.MaxAttempts, callerId,
                idempotencyKey, Activity.Current?.Id, subject.SubjectId);
            var result = await runs.SubmitAsync(TenantContext.ForOrganization(org, actor.SubjectId), request, IdempotencyRetention, ct);

            return result.Status switch
            {
                SubmitStatus.Conflict => Problems.Conflict("idempotency.conflict", "This Idempotency-Key was used with a different payload."),
                SubmitStatus.Existing => Results.Ok(RunResponse.From(result.Run!, includeOutput: true)),
                _ => Results.Accepted(
                    $"/v1/organizations/{organizationId}/workspaces/{workspaceId}/runs/{RunResponse.From(result.Run!, false).Id}",
                    RunResponse.From(result.Run!, includeOutput: true)),
            };
        });

        v1.MapGet(Base, async (
            string organizationId, string workspaceId, HttpContext http,
            IControlPlaneStore controlPlane, IRunStore runs, CancellationToken ct) =>
        {
            var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(subject!, ws, Permission.RunsRead) is { } denied)
            {
                return denied;
            }

            var list = await runs.ListRunsAsync(org, subject!.SubjectId, ws, MaxListLimit, ct);
            return Results.Ok(list.Select(r => RunResponse.From(r, includeOutput: false)).ToList());
        });

        v1.MapGet($"{Base}/{{runId}}", async (
            string organizationId, string workspaceId, string runId, HttpContext http,
            IControlPlaneStore controlPlane, IRunStore runs, CancellationToken ct) =>
        {
            var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(subject!, ws, Permission.RunsRead) is { } denied)
            {
                return denied;
            }

            if (!ResourceId.TryParse(ResourceId.Run, runId, out var run))
            {
                return Problems.NotFound();
            }

            var view = await runs.GetRunAsync(org, subject!.SubjectId, ws, run, ct);
            if (view is null)
            {
                return Problems.NotFound();
            }

            var includeOutput = subject.EffectiveInWorkspace(ws).Contains(Permission.RunsReadResult);
            return Results.Ok(RunResponse.From(view, includeOutput));
        });

        v1.MapPost($"{Base}/{{runId}}/cancel", async (
            string organizationId, string workspaceId, string runId, HttpContext http,
            IControlPlaneStore controlPlane, IRunStore runs, CancellationToken ct) =>
        {
            var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(subject!, ws, Permission.RunsCancel) is { } denied)
            {
                return denied;
            }

            if (!ResourceId.TryParse(ResourceId.Run, runId, out var run))
            {
                return Problems.NotFound();
            }

            var view = await runs.CancelRunAsync(EndpointAuth.Actor(http), org, ws, run, ct);
            return view is null ? Problems.NotFound() : Results.Accepted(value: RunResponse.From(view, includeOutput: false));
        });

        return v1;
    }
}

public sealed record SubmitRunBody(string? TaskDefinitionId, JsonElement? Input, int? Version);

public sealed record RunResponse(
    string Id,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    JsonElement? Output,
    string? ErrorCategory,
    string? ErrorMessage,
    bool ProviderCallMayHaveCompleted,
    DateTimeOffset? CancelRequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt)
{
    public static RunResponse From(RunView view, bool includeOutput) => new(
        ResourceId.Format(ResourceId.Run, view.Id),
        view.Status,
        view.AttemptCount,
        view.MaxAttempts,
        includeOutput && view.Output is not null ? JsonSerializer.Deserialize<JsonElement>(view.Output) : null,
        view.ErrorCategory,
        view.ErrorMessage,
        view.ProviderCallMayHaveCompleted,
        view.CancelRequestedAt,
        view.StartedAt,
        view.CompletedAt,
        view.CreatedAt);
}
