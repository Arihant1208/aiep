using System.Security.Claims;
using Aiep.Api.Authentication;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary><c>GET /v1/me</c>: who the API thinks you are. A debugging aid for callers (ADR-0010).</summary>
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
        return v1;
    }
}

public sealed record MeResponse(string SubjectId, string AuthenticationMethod, MeApiKey? ApiKey);

public sealed record MeApiKey(string Id, string OrganizationId, string? WorkspaceId, IReadOnlyList<string> Permissions);
