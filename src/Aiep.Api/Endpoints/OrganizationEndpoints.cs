using Aiep.Api.Authentication;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary>Organizations (ADR-0015): self-service create, and read for members with <c>org:read</c>.</summary>
public static class OrganizationEndpoints
{
    private const int MaxNameLength = 200;

    public static RouteGroupBuilder MapOrganizationEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/organizations", async (
            CreateOrganizationRequest request, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            // API keys are bound to one organization (ADR-0010) and cannot create another.
            if (CallerIdentity.From(http.User).ApiKeyId is not null)
            {
                return Problems.Forbidden("API keys cannot create organizations.");
            }

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return Problems.BadRequest($"name is required and must be 1-{MaxNameLength} characters.");
            }

            var created = await store.CreateOrganizationAsync(EndpointAuth.Actor(http), name, ct);
            var response = OrganizationResponse.From(created);
            return Results.Created($"/v1/organizations/{response.Id}", response);
        });

        v1.MapGet("/organizations/{id}", async (
            string id, HttpContext http, IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, id, out var organizationId))
            {
                return Problems.NotFound();
            }

            var (subject, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organizationId, Permission.OrgRead, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            var organization = await store.GetOrganizationAsync(organizationId, subject!.SubjectId, ct);
            return organization is null ? Problems.NotFound() : Results.Ok(OrganizationResponse.From(organization));
        });

        return v1;
    }
}

public sealed record CreateOrganizationRequest(string? Name);

public sealed record OrganizationResponse(string Id, string Name, string Status, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse From(OrganizationView view) =>
        new(ResourceId.Format(ResourceId.Organization, view.Id), view.Name, view.Status, view.CreatedAt);
}
