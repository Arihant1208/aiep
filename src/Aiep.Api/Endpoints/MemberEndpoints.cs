using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Ids;

namespace Aiep.Api.Endpoints;

/// <summary>Organization members (ADR-0010, ADR-0015): list, add, change role, remove. Owner-protection and the
/// last-Owner invariant are enforced in the store.</summary>
public static class MemberEndpoints
{
    public static RouteGroupBuilder MapMemberEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/organizations/{organizationId}/members", async (
            string organizationId, HttpContext http, IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
            {
                return Problems.NotFound();
            }

            var (subject, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
                http, organization, Permission.MembersRead, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            var members = await store.ListMembersAsync(organization, subject!.SubjectId, ct);
            return Results.Ok(members.Select(MemberResponse.From).ToList());
        });

        v1.MapPost("/organizations/{organizationId}/members", async (
            string organizationId, AddMemberRequest request, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            var (subject, organization, failure) = await AuthorizeManageAsync(organizationId, http, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (!ResourceId.TryParse(ResourceId.Subject, request.SubjectId, out var target))
            {
                return Problems.NotFound();
            }

            if (!Roles.TryParseOrgRole(request.Role, out var role))
            {
                return Problems.BadRequest("role must be one of Owner, Admin, Auditor, Member.");
            }

            var result = await store.AddMemberAsync(
                EndpointAuth.Actor(http), organization, subject!.OrgRole!.Value, target, role, ct);
            return result.Outcome == MemberChangeOutcome.Ok
                ? Results.Created(
                    $"/v1/organizations/{organizationId}/members/{request.SubjectId}", MemberResponse.From(result.Member!))
                : MapFailure(result.Outcome);
        });

        v1.MapPut("/organizations/{organizationId}/members/{subjectId}", async (
            string organizationId, string subjectId, ChangeRoleRequest request, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            var (subject, organization, failure) = await AuthorizeManageAsync(organizationId, http, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (!ResourceId.TryParse(ResourceId.Subject, subjectId, out var target))
            {
                return Problems.NotFound();
            }

            if (!Roles.TryParseOrgRole(request.Role, out var role))
            {
                return Problems.BadRequest("role must be one of Owner, Admin, Auditor, Member.");
            }

            var result = await store.ChangeMemberRoleAsync(
                EndpointAuth.Actor(http), organization, subject!.OrgRole!.Value, target, role, ct);
            return result.Outcome == MemberChangeOutcome.Ok
                ? Results.Ok(MemberResponse.From(result.Member!))
                : MapFailure(result.Outcome);
        });

        v1.MapDelete("/organizations/{organizationId}/members/{subjectId}", async (
            string organizationId, string subjectId, HttpContext http,
            IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct) =>
        {
            var (subject, organization, failure) = await AuthorizeManageAsync(organizationId, http, store, authorization, ct);
            if (failure is not null)
            {
                return failure;
            }

            if (!ResourceId.TryParse(ResourceId.Subject, subjectId, out var target))
            {
                return Problems.NotFound();
            }

            var result = await store.RemoveMemberAsync(
                EndpointAuth.Actor(http), organization, subject!.OrgRole!.Value, target, ct);
            return result.Outcome == MemberChangeOutcome.Ok ? Results.NoContent() : MapFailure(result.Outcome);
        });

        return v1;
    }

    private static async Task<(AuthorizationSubject? Subject, Guid Organization, IResult? Failure)> AuthorizeManageAsync(
        string organizationId, HttpContext http, IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct)
    {
        if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization))
        {
            return (null, default, Problems.NotFound());
        }

        var (subject, failure) = await EndpointAuth.AuthorizeOrganizationAsync(
            http, organization, Permission.MembersManage, store, authorization, ct);
        return (subject, organization, failure);
    }

    private static IResult MapFailure(MemberChangeOutcome outcome) => outcome switch
    {
        MemberChangeOutcome.SubjectNotFound => Problems.NotFound(),
        MemberChangeOutcome.NotAMember => Problems.NotFound(),
        MemberChangeOutcome.AlreadyMember => Problems.Conflict("member.already_exists", "That subject is already a member."),
        MemberChangeOutcome.OwnerProtected => Problems.Forbidden("Only an Owner may grant, change or remove an Owner."),
        MemberChangeOutcome.LastOwner => Problems.Conflict("organization.last_owner", "An organization must keep at least one active Owner."),
        _ => Problems.Forbidden(),
    };
}

public sealed record AddMemberRequest(string? SubjectId, string? Role);

public sealed record ChangeRoleRequest(string? Role);

public sealed record MemberResponse(
    string SubjectId, string OrgRole, string Status, string? DisplayName, string? Email, DateTimeOffset CreatedAt)
{
    public static MemberResponse From(MemberView view) => new(
        ResourceId.Format(ResourceId.Subject, view.SubjectId),
        view.OrgRole, view.Status, view.DisplayName, view.Email, view.CreatedAt);
}
