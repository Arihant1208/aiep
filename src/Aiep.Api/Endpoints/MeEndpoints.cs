using System.Security.Claims;
using Aiep.Api.Authentication;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary><c>GET /v1/me</c> and <c>/v1/me/permissions</c>: who the API thinks you are, and what you can do (ADR-0010).</summary>
public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me", (ClaimsPrincipal user) =>
        {
            var caller = CallerIdentity.From(user);
            var apiKey = caller.ApiKeyId is { } keyId
                ? new MeApiKey(
                    ResourceId.Format(ResourceId.ApiKey, keyId),
                    ResourceId.Format(ResourceId.Organization, caller.OrganizationId!.Value),
                    caller.WorkspaceId is { } ws ? ResourceId.Format(ResourceId.Workspace, ws) : null,
                    caller.KeyPermissions)
                : null;
            return new MeResponse(
                ResourceId.Format(ResourceId.Subject, caller.SubjectId), caller.AuthenticationMethod, apiKey);
        });

        // The ADR-0010 debugging aid: the caller's effective permissions in one organization. A user names the
        // organization with ?organization=org_...; an API key reports its own organization.
        v1.MapGet("/me/permissions", async (
            HttpContext http, string? organization, IControlPlaneStore store, CancellationToken ct) =>
        {
            var caller = CallerIdentity.From(http.User);
            Guid organizationId;
            if (caller.OrganizationId is { } keyOrganization)
            {
                if (organization is not null
                    && (!ResourceId.TryParse(ResourceId.Organization, organization, out var requested) || requested != keyOrganization))
                {
                    return Problems.NotFound();
                }

                organizationId = keyOrganization;
            }
            else if (organization is null)
            {
                return Problems.BadRequest("organization query parameter is required.");
            }
            else if (!ResourceId.TryParse(ResourceId.Organization, organization, out organizationId))
            {
                return Problems.NotFound();
            }

            var subject = await EndpointAuth.ResolveAsync(http, organizationId, store, ct);
            if (subject is null || !subject.IsActiveMember)
            {
                return Problems.NotFound();
            }

            var workspaces = subject.WorkspaceGrants
                .Select(grant => new MeWorkspacePermissions(
                    ResourceId.Format(ResourceId.Workspace, grant.Key),
                    grant.Value.ToString(),
                    [.. subject.EffectiveInWorkspace(grant.Key).Order(StringComparer.Ordinal)]))
                .OrderBy(w => w.WorkspaceId, StringComparer.Ordinal)
                .ToList();

            return Results.Ok(new MePermissionsResponse(
                ResourceId.Format(ResourceId.Organization, organizationId),
                subject.OrgRole!.Value.ToString(),
                [.. subject.EffectiveAtOrganization().Order(StringComparer.Ordinal)],
                subject.OrgRole is OrgRole.Owner or OrgRole.Admin,
                workspaces));
        });

        return v1;
    }
}

public sealed record MeResponse(string SubjectId, string AuthenticationMethod, MeApiKey? ApiKey);

public sealed record MeApiKey(string Id, string OrganizationId, string? WorkspaceId, IReadOnlyList<string> Permissions);

public sealed record MePermissionsResponse(
    string OrganizationId,
    string OrgRole,
    IReadOnlyList<string> Permissions,
    bool WorkspaceAdminEverywhere,
    IReadOnlyList<MeWorkspacePermissions> Workspaces);

public sealed record MeWorkspacePermissions(string WorkspaceId, string Role, IReadOnlyList<string> Permissions);
