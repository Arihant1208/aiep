using Aiep.Api.Authentication;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Identity;
using Aiep.Application.Ids;
using Microsoft.Extensions.Options;

namespace Aiep.Api.Endpoints;

/// <summary>
/// API keys (ADR-0008, ADR-0015) under <c>/v1/organizations/{id}/api-keys</c>: create (the secret is returned once and
/// never again), list (metadata only), revoke. A key's scope must be within the creator's own permissions.
/// </summary>
public static class ApiKeyEndpoints
{
    private const int MaxNameLength = 200;

    public static RouteGroupBuilder MapApiKeyEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/organizations/{organizationId}/api-keys", async (
            string organizationId, CreateApiKeyRequest request, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, ApiKeyHasher hasher,
            IOptions<ApiKeySettings> settings, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return Problems.BadRequest($"name is required and must be 1-{MaxNameLength} characters.");
            }

            var permissions = request.Permissions;
            if (permissions is null || permissions.Count == 0)
            {
                return Problems.BadRequest("permissions must list at least one permission.");
            }

            if (permissions.Any(p => !Permission.IsKnown(p)))
            {
                return Problems.BadRequest("permissions contains an unknown permission.");
            }

            // The key's ceiling is the creator's effective permissions at the key's scope (org-wide, or one workspace).
            IReadOnlySet<string> ceiling;
            Guid? workspaceId = null;
            if (request.WorkspaceId is not null)
            {
                if (!ResourceId.TryParse(ResourceId.Workspace, request.WorkspaceId, out var workspace))
                {
                    return Problems.NotFound();
                }

                var resolved = await EndpointAuth.ResolveAsync(http, organization, store, ct);
                if (resolved is null || !resolved.IsActiveMember
                    || await store.GetWorkspaceAsync(organization, resolved.SubjectId, workspace, ct) is null)
                {
                    return Problems.NotFound();
                }

                if (EndpointAuth.CheckWorkspace(resolved, workspace, Permission.KeysCreate) is { } denied)
                {
                    return denied;
                }

                ceiling = resolved.EffectiveInWorkspace(workspace);
                workspaceId = workspace;
            }
            else
            {
                var (subject, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                    http, organization, Permission.KeysCreate, store, authorization, ct);
                if (failure is not null)
                {
                    return failure;
                }

                ceiling = subject!.EffectiveAtOrganization();
            }

            // No one grants a permission they don't hold (ADR-0008, ADR-0010).
            var exceeding = AuthorizationInvariants.ScopeExceeding(ceiling, permissions);
            if (exceeding.Count > 0)
            {
                return Problems.Forbidden($"The key scope exceeds your permissions: {string.Join(", ", exceeding)}.");
            }

            var generated = ApiKeyFormat.Generate(settings.Value.Environment);
            var view = await store.CreateApiKeyAsync(
                EndpointAuth.Actor(http), organization, workspaceId, generated.KeyId, hasher.Hash(generated.Secret),
                settings.Value.Environment, name, permissions, request.ExpiresAt, ct);

            var response = new CreatedApiKeyResponse(ApiKeyResponse.From(view), generated.Plaintext);
            return Results.Created($"/v1/organizations/{organizationId}/api-keys/{response.Key.Id}", response);
        });

        v1.MapGet("/organizations/{organizationId}/api-keys", async (
            string organizationId, HttpContext http, IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var (subject, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organization, Permission.KeysRead, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            var keys = await store.ListApiKeysAsync(organization, subject!.SubjectId, ct);
            return Results.Ok(keys.Select(ApiKeyResponse.From).ToList());
        });

        v1.MapPost("/organizations/{organizationId}/api-keys/{apiKeyId}/revoke", async (
            string organizationId, string apiKeyId, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization)
                || !ResourceId.TryParse(ResourceId.ApiKey, apiKeyId, out var key))
            {
                return Problems.NotFound();
            }

            var (_, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organization, Permission.KeysRevoke, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            var outcome = await store.RevokeApiKeyAsync(EndpointAuth.Actor(http), organization, key, ct);
            return outcome == ApiKeyRevokeOutcome.NotFound ? Problems.NotFound() : Results.NoContent();
        });

        return v1;
    }
}

public sealed record CreateApiKeyRequest(
    string? Name, string? WorkspaceId, IReadOnlyList<string>? Permissions, DateTimeOffset? ExpiresAt);

public sealed record ApiKeyResponse(
    string Id,
    string Name,
    string Environment,
    string? WorkspaceId,
    string OwnerSubjectId,
    IReadOnlyList<string> Permissions,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt)
{
    public static ApiKeyResponse From(ApiKeyView view) => new(
        ResourceId.Format(ResourceId.ApiKey, view.Id),
        view.Name,
        view.Environment,
        view.WorkspaceId is { } ws ? ResourceId.Format(ResourceId.Workspace, ws) : null,
        ResourceId.Format(ResourceId.Subject, view.OwnerSubjectId),
        view.Permissions,
        view.ExpiresAt,
        view.LastUsedAt,
        view.RevokedAt,
        view.CreatedAt);
}

/// <summary>The one and only time the secret is returned. Store it now; it cannot be retrieved again.</summary>
public sealed record CreatedApiKeyResponse(ApiKeyResponse Key, string Secret);
