using System.Text.RegularExpressions;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary>
/// Workspaces and their role grants (ADR-0010, ADR-0015), under <c>/v1/organizations/{organizationId}/workspaces</c>.
/// A workspace the caller has no permission in is concealed as 404.
/// </summary>
public static partial class WorkspaceEndpoints
{
    private const int MaxNameLength = 200;

    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/organizations/{organizationId}/workspaces", async (
            string organizationId, CreateWorkspaceRequest request, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var (_, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organization, Permission.WorkspacesCreate, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
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

            var created = await store.CreateWorkspaceAsync(EndpointAuth.Actor(http), organization, slug, name, ct);
            if (created is null)
            {
                return Problems.Conflict("workspace.slug_taken", $"A workspace with slug '{slug}' already exists.");
            }

            var response = WorkspaceResponse.From(created);
            return Results.Created($"/v1/organizations/{organizationId}/workspaces/{response.Id}", response);
        });

        v1.MapGet("/organizations/{organizationId}/workspaces", async (
            string organizationId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var subject = await EndpointAuth.ResolveAsync(http, organization, store, ct);
            if (subject is null || !subject.IsActiveMember)
            {
                return Problems.NotFound();
            }

            var all = await store.ListWorkspacesAsync(organization, subject.SubjectId, ct);
            var visible = all
                .Where(w => EndpointAuth.CanSeeWorkspace(subject, w.Id))
                .Select(WorkspaceResponse.From)
                .ToList();
            return Results.Ok(visible);
        });

        v1.MapGet("/organizations/{organizationId}/workspaces/{workspaceId}", async (
            string organizationId, string workspaceId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            var (resolved, ids, failure) = await ResolveWorkspaceAsync(organizationId, workspaceId, http, store, ct);
            if (failure is not null)
            {
                return failure;
            }

            return EndpointAuth.CanSeeWorkspace(resolved!, ids.Workspace)
                ? Results.Ok(WorkspaceResponse.From(ids.Found!))
                : Problems.NotFound();
        });

        v1.MapPost("/organizations/{organizationId}/workspaces/{workspaceId}/archive", async (
            string organizationId, string workspaceId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            var (resolved, ids, failure) = await ResolveWorkspaceAsync(organizationId, workspaceId, http, store, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (EndpointAuth.CheckWorkspace(resolved!, ids.Workspace, Permission.WorkspacesManage) is { } denied)
            {
                return denied;
            }

            var archived = await store.ArchiveWorkspaceAsync(EndpointAuth.Actor(http), ids.Organization, ids.Workspace, ct);
            return archived is null ? Problems.NotFound() : Results.Ok(WorkspaceResponse.From(archived));
        });

        v1.MapPut("/organizations/{organizationId}/workspaces/{workspaceId}/grants/{subjectId}", async (
            string organizationId, string workspaceId, string subjectId, SetGrantRequest request, HttpContext http,
            IControlPlaneStore store, CancellationToken ct) =>
        {
            var (resolved, ids, failure) = await ResolveWorkspaceAsync(organizationId, workspaceId, http, store, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (!ResourceId.TryParse(ResourceId.Subject, subjectId, out var target))
            {
                return Problems.NotFound();
            }

            if (EndpointAuth.CheckWorkspace(resolved!, ids.Workspace, Permission.GrantsManage) is { } denied)
            {
                return denied;
            }

            if (!Roles.TryParseWorkspaceRole(request.Role, out var role))
            {
                return Problems.BadRequest("role must be one of WorkspaceAdmin, Developer, Evaluator, Viewer.");
            }

            // No one grants a permission they don't hold (authorization-model invariant).
            if (!AuthorizationInvariants.CanDelegate(
                    resolved!.EffectiveInWorkspace(ids.Workspace), RolePermissions.ForWorkspaceRole(role)))
            {
                return Problems.Forbidden("Cannot grant a role with permissions you do not hold.");
            }

            var grant = await store.SetWorkspaceGrantAsync(EndpointAuth.Actor(http), ids.Organization, ids.Workspace, target, role, ct);
            return grant is null
                ? Problems.NotFound() // the subject is not a member of this organization
                : Results.Ok(WorkspaceGrantResponse.From(grant));
        });

        v1.MapDelete("/organizations/{organizationId}/workspaces/{workspaceId}/grants/{subjectId}", async (
            string organizationId, string workspaceId, string subjectId, HttpContext http,
            IControlPlaneStore store, CancellationToken ct) =>
        {
            var (resolved, ids, failure) = await ResolveWorkspaceAsync(organizationId, workspaceId, http, store, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (!ResourceId.TryParse(ResourceId.Subject, subjectId, out var target))
            {
                return Problems.NotFound();
            }

            if (EndpointAuth.CheckWorkspace(resolved!, ids.Workspace, Permission.GrantsManage) is { } denied)
            {
                return denied;
            }

            return await store.RemoveWorkspaceGrantAsync(EndpointAuth.Actor(http), ids.Organization, ids.Workspace, target, ct)
                ? Results.NoContent()
                : Problems.NotFound();
        });

        return v1;
    }

    // Shared prelude for workspace-scoped endpoints: parse ids, confirm membership, confirm the workspace exists.
    private static async Task<(AuthorizationSubject? Subject, WorkspaceIds Ids, IResult? Failure)> ResolveWorkspaceAsync(
        string organizationId, string workspaceId, HttpContext http, IControlPlaneStore store, CancellationToken ct)
    {
        if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization)
            || !ResourceId.TryParse(ResourceId.Workspace, workspaceId, out var workspace))
        {
            return (null, default, Problems.NotFound());
        }

        var subject = await EndpointAuth.ResolveAsync(http, organization, store, ct);
        if (subject is null || !subject.IsActiveMember)
        {
            return (null, default, Problems.NotFound());
        }

        var found = await store.GetWorkspaceAsync(organization, subject.SubjectId, workspace, ct);
        return found is null
            ? (null, default, Problems.NotFound())
            : (subject, new WorkspaceIds(organization, workspace, found), null);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    private readonly record struct WorkspaceIds(Guid Organization, Guid Workspace, WorkspaceView? Found);
}

public sealed record CreateWorkspaceRequest(string? Slug, string? Name);

public sealed record SetGrantRequest(string? Role);

public sealed record WorkspaceResponse(
    string Id, string Slug, string Name, string Status, DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt)
{
    public static WorkspaceResponse From(WorkspaceView view) => new(
        ResourceId.Format(ResourceId.Workspace, view.Id), view.Slug, view.Name, view.Status, view.ArchivedAt, view.CreatedAt);
}

public sealed record WorkspaceGrantResponse(string WorkspaceId, string SubjectId, string Role)
{
    public static WorkspaceGrantResponse From(WorkspaceGrantView view) => new(
        ResourceId.Format(ResourceId.Workspace, view.WorkspaceId),
        ResourceId.Format(ResourceId.Subject, view.SubjectId),
        view.Role);
}
