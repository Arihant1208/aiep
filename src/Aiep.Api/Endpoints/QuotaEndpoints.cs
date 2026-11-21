using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary>
/// Organization spend budget and usage (threat T12, ADR-0015). Setting the budget needs <c>org:manage</c>; reading usage
/// needs <c>usage:read</c>. Enforcement happens reserve-then-settle at run submission.
/// </summary>
public static class QuotaEndpoints
{
    public static RouteGroupBuilder MapQuotaEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPut("/organizations/{organizationId}/budget", async (
            string organizationId, SetBudgetRequest request, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, IQuota quota, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var (_, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organization, Permission.OrgManage, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (request.WindowSeconds is not (>= 60 and <= 2678400))
            {
                return Problems.BadRequest("windowSeconds must be between 60 and 2678400 (31 days).");
            }

            if (request.LimitUsd is not (>= 0m and <= 100000000m))
            {
                return Problems.BadRequest("limitUsd must be between 0 and 100000000.");
            }

            await quota.SetOrganizationBudgetAsync(EndpointAuth.Actor(http), organization, request.WindowSeconds, request.LimitUsd, ct);
            var usage = await quota.GetOrganizationUsageAsync(organization, EndpointAuth.Actor(http).SubjectId, ct);
            return Results.Ok(QuotaUsageResponse.From(usage));
        });

        v1.MapGet("/organizations/{organizationId}/usage", async (
            string organizationId, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, IQuota quota, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var (subject, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organization, Permission.UsageRead, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            var usage = await quota.GetOrganizationUsageAsync(organization, subject!.SubjectId, ct);
            return Results.Ok(QuotaUsageResponse.From(usage));
        });

        return v1;
    }
}

public sealed record SetBudgetRequest(int WindowSeconds, decimal LimitUsd);

public sealed record QuotaUsageResponse(bool HasBudget, int WindowSeconds, decimal LimitUsd, decimal CommittedUsd)
{
    public static QuotaUsageResponse From(QuotaUsage usage) =>
        new(usage.HasBudget, usage.WindowSeconds, usage.LimitUsd, usage.CommittedUsd);
}
