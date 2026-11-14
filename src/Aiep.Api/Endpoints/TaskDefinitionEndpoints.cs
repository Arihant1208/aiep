using System.Text.Json;
using System.Text.RegularExpressions;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary>
/// Task definitions and their immutable versions (ADR-0007, ADR-0015), workspace-scoped under
/// <c>/v1/organizations/{organizationId}/workspaces/{workspaceId}/task-definitions</c>. A workspace the caller cannot see
/// is concealed as 404; a handler that is not allowlisted is rejected at authoring time.
/// </summary>
public static partial class TaskDefinitionEndpoints
{
    private const int MaxNameLength = 200;

    public static RouteGroupBuilder MapTaskDefinitionEndpoints(this RouteGroupBuilder v1)
    {
        const string Base = "/organizations/{organizationId}/workspaces/{workspaceId}/task-definitions";

        v1.MapPost(Base, async (
            string organizationId, string workspaceId, CreateTaskDefinitionRequest request, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(subject!, ws, Permission.DefinitionsWrite) is { } denied)
            {
                return denied;
            }

            var slug = request.Slug?.Trim();
            var name = request.Name?.Trim();
            if (slug is null || !SlugPattern().IsMatch(slug))
            {
                return Problems.BadRequest("slug must be 1-63 characters, lowercase letters, digits and hyphens.");
            }

            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return Problems.BadRequest($"name is required and must be 1-{MaxNameLength} characters.");
            }

            var created = await catalog.CreateDefinitionAsync(EndpointAuth.Actor(http), org, ws, slug, name, request.Description?.Trim(), ct);
            if (created is null)
            {
                return Problems.Conflict("definition.slug_taken", $"A task definition with slug '{slug}' already exists.");
            }

            var response = TaskDefinitionResponse.From(created);
            return Results.Created($"/v1/organizations/{organizationId}/workspaces/{workspaceId}/task-definitions/{response.Id}", response);
        });

        v1.MapGet(Base, async (
            string organizationId, string workspaceId, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(subject!, ws, Permission.DefinitionsRead) is { } denied)
            {
                return denied;
            }

            var definitions = await catalog.ListDefinitionsAsync(org, subject!.SubjectId, ws, ct);
            return Results.Ok(definitions.Select(TaskDefinitionResponse.From).ToList());
        });

        v1.MapGet($"{Base}/{{definitionId}}", async (
            string organizationId, string workspaceId, string definitionId, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var (subject, org, ws, def, failure) = await ResolveDefinitionAsync(
                organizationId, workspaceId, definitionId, Permission.DefinitionsRead, http, controlPlane, catalog, ct);
            return failure ?? Results.Ok(TaskDefinitionResponse.From(def!));
        });

        v1.MapPost($"{Base}/{{definitionId}}/versions", async (
            string organizationId, string workspaceId, string definitionId, CreateVersionRequest request, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, IHandlerRegistry handlers, CancellationToken ct) =>
        {
            var (subject, org, ws, def, failure) = await ResolveDefinitionAsync(
                organizationId, workspaceId, definitionId, Permission.DefinitionsWrite, http, controlPlane, catalog, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (ValidateVersion(request, handlers, out var spec, out var badRequest) is false)
            {
                return badRequest!;
            }

            var created = await catalog.CreateVersionAsync(EndpointAuth.Actor(http), org, ws, def!.Id, spec!, ct);
            return created is null
                ? Problems.NotFound()
                : Results.Created(
                    $"/v1/organizations/{organizationId}/workspaces/{workspaceId}/task-definitions/{definitionId}/versions/{created.Version}",
                    TaskDefinitionVersionResponse.From(created));
        });

        v1.MapGet($"{Base}/{{definitionId}}/versions", async (
            string organizationId, string workspaceId, string definitionId, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var (subject, org, ws, def, failure) = await ResolveDefinitionAsync(
                organizationId, workspaceId, definitionId, Permission.DefinitionsRead, http, controlPlane, catalog, ct);
            if (failure is not null)
            {
                return failure;
            }

            var versions = await catalog.ListVersionsAsync(org, subject!.SubjectId, ws, def!.Id, ct);
            return Results.Ok(versions.Select(TaskDefinitionVersionResponse.From).ToList());
        });

        v1.MapGet($"{Base}/{{definitionId}}/versions/{{version:int}}", async (
            string organizationId, string workspaceId, string definitionId, int version, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var (subject, org, ws, def, failure) = await ResolveDefinitionAsync(
                organizationId, workspaceId, definitionId, Permission.DefinitionsRead, http, controlPlane, catalog, ct);
            if (failure is not null)
            {
                return failure;
            }

            var found = await catalog.GetVersionAsync(org, subject!.SubjectId, ws, def!.Id, version, ct);
            return found is null ? Problems.NotFound() : Results.Ok(TaskDefinitionVersionResponse.From(found));
        });

        v1.MapPost($"{Base}/{{definitionId}}/versions/{{version:int}}/publish", async (
            string organizationId, string workspaceId, string definitionId, int version, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct) =>
        {
            var (subject, org, ws, def, failure) = await ResolveDefinitionAsync(
                organizationId, workspaceId, definitionId, Permission.DefinitionsWrite, http, controlPlane, catalog, ct);
            if (failure is not null)
            {
                return failure;
            }

            var published = await catalog.PublishVersionAsync(EndpointAuth.Actor(http), org, ws, def!.Id, version, ct);
            return published is null ? Problems.NotFound() : Results.Ok(TaskDefinitionVersionResponse.From(published));
        });

        return v1;
    }

    private static async Task<(AuthorizationSubject? Subject, Guid Organization, Guid Workspace, TaskDefinitionView? Definition, IResult? Failure)>
        ResolveDefinitionAsync(
            string organizationId, string workspaceId, string definitionId, string permission, HttpContext http,
            IControlPlaneStore controlPlane, ITaskCatalogStore catalog, CancellationToken ct)
    {
        var (subject, org, ws, failure) = await EndpointAuth.ResolveWorkspaceAsync(organizationId, workspaceId, http, controlPlane, ct);
        if (failure is not null)
        {
            return (null, default, default, null, failure);
        }

        if (EndpointAuth.CheckWorkspace(subject!, ws, permission) is { } denied)
        {
            return (null, default, default, null, denied);
        }

        if (!ResourceId.TryParse(ResourceId.TaskDefinition, definitionId, out var definition))
        {
            return (null, default, default, null, Problems.NotFound());
        }

        var found = await catalog.GetDefinitionAsync(org, subject!.SubjectId, ws, definition, ct);
        return found is null
            ? (null, default, default, null, Problems.NotFound())
            : (subject, org, ws, found, null);
    }

    private static bool ValidateVersion(
        CreateVersionRequest request, IHandlerRegistry handlers, out NewVersionSpec? spec, out IResult? badRequest)
    {
        spec = null;
        badRequest = null;

        var handlerType = request.HandlerType?.Trim();
        var handlerVersion = request.HandlerVersion?.Trim();
        var modelProvider = request.ModelProvider?.Trim();
        var modelName = request.ModelName?.Trim();

        if (string.IsNullOrEmpty(handlerType) || string.IsNullOrEmpty(handlerVersion))
        {
            badRequest = Problems.BadRequest("handlerType and handlerVersion are required.");
            return false;
        }

        if (handlers.Resolve(handlerType, handlerVersion) is null)
        {
            badRequest = Problems.BadRequest($"Handler '{handlerType}' version '{handlerVersion}' is not allowlisted.");
            return false;
        }

        if (string.IsNullOrEmpty(modelProvider) || string.IsNullOrEmpty(modelName))
        {
            badRequest = Problems.BadRequest("modelProvider and modelName are required.");
            return false;
        }

        var maxAttempts = request.MaxAttempts ?? 3;
        var timeoutSeconds = request.TimeoutSeconds ?? 60;
        var leaseSeconds = request.LeaseSeconds ?? 60;
        if (maxAttempts is < 1 or > 10)
        {
            badRequest = Problems.BadRequest("maxAttempts must be between 1 and 10.");
            return false;
        }

        if (timeoutSeconds is < 1 or > 3600)
        {
            badRequest = Problems.BadRequest("timeoutSeconds must be between 1 and 3600.");
            return false;
        }

        if (leaseSeconds is < 5 or > 3600)
        {
            badRequest = Problems.BadRequest("leaseSeconds must be between 5 and 3600.");
            return false;
        }

        spec = new NewVersionSpec(
            Raw(request.InputSchema, "{}"),
            Raw(request.OutputSchema, "{}"),
            handlerType,
            handlerVersion,
            modelProvider,
            modelName,
            Raw(request.ModelConfig, "{}"),
            string.IsNullOrWhiteSpace(request.PromptTemplate) ? null : request.PromptTemplate,
            maxAttempts,
            timeoutSeconds,
            leaseSeconds);
        return true;
    }

    private static string Raw(JsonElement? element, string fallback) =>
        element is { } e && e.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            ? e.GetRawText()
            : fallback;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();
}

public sealed record CreateTaskDefinitionRequest(string? Slug, string? Name, string? Description);

public sealed record CreateVersionRequest(
    JsonElement? InputSchema,
    JsonElement? OutputSchema,
    string? HandlerType,
    string? HandlerVersion,
    string? ModelProvider,
    string? ModelName,
    JsonElement? ModelConfig,
    string? PromptTemplate,
    int? MaxAttempts,
    int? TimeoutSeconds,
    int? LeaseSeconds);

public sealed record TaskDefinitionResponse(
    string Id, string Slug, string Name, string? Description, string Status, int? CurrentVersion, DateTimeOffset CreatedAt)
{
    public static TaskDefinitionResponse From(TaskDefinitionView view) => new(
        ResourceId.Format(ResourceId.TaskDefinition, view.Id),
        view.Slug, view.Name, view.Description, view.Status, view.CurrentVersion, view.CreatedAt);
}

public sealed record TaskDefinitionVersionResponse(
    string DefinitionId, int Version, string Status, string HandlerType, string HandlerVersion,
    string ModelProvider, string ModelName, int MaxAttempts, int TimeoutSeconds, int LeaseSeconds,
    DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt)
{
    public static TaskDefinitionVersionResponse From(TaskDefinitionVersionView view) => new(
        ResourceId.Format(ResourceId.TaskDefinition, view.DefinitionId), view.Version, view.Status,
        view.HandlerType, view.HandlerVersion, view.ModelProvider, view.ModelName,
        view.MaxAttempts, view.TimeoutSeconds, view.LeaseSeconds, view.PublishedAt, view.CreatedAt);
}
