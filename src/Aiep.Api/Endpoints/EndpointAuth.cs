using Aiep.Api.Authentication;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;

namespace Aiep.Api.Endpoints;

/// <summary>
/// The shared front half of the request algorithm (<c>docs/security/authorization-model.md</c>) for control-plane
/// endpoints: identify the caller, resolve their roles in the target organization (step 3), and check the required
/// permission (step 5). Denials map to the ADR-0015 status: not a member → 404 (the organization is concealed),
/// missing permission → 403.
/// </summary>
internal static class EndpointAuth
{
    public static ControlPlaneActor Actor(HttpContext http)
    {
        var caller = CallerIdentity.From(http.User);
        return new ControlPlaneActor(caller.SubjectId, caller.ApiKeyId, http.TraceIdentifier);
    }

    /// <summary>
    /// Resolve and authorize the caller for an organization-level permission. On success returns the subject and a null
    /// failure; on denial returns a null subject and the <see cref="IResult"/> to send back.
    /// </summary>
    public static async Task<(AuthorizationSubject? Subject, IResult? Failure)> AuthorizeOrganizationAsync(
        HttpContext http, Guid targetOrganization, string permission,
        IControlPlaneStore store, IAuthorizationService authorization, CancellationToken ct)
    {
        var subject = await ResolveAsync(http, targetOrganization, store, ct);
        if (subject is null)
        {
            // An API key for a different organization: that organization is invisible to it.
            return (null, Problems.NotFound());
        }

        var decision = authorization.AuthorizeOrganization(subject, permission);
        return decision.IsAllowed ? (subject, null) : (null, Fault(decision.Failure!.Value));
    }

    /// <summary>
    /// Resolve the caller's roles in an organization with no permission gate (for self-describing endpoints such as
    /// <c>/me/permissions</c>). Returns null when an API key is scoped to a different organization.
    /// </summary>
    public static async Task<AuthorizationSubject?> ResolveAsync(
        HttpContext http, Guid targetOrganization, IControlPlaneStore store, CancellationToken ct)
    {
        var caller = CallerIdentity.From(http.User);
        if (caller.OrganizationId is { } keyOrganization && keyOrganization != targetOrganization)
        {
            return null;
        }

        return await store.ResolveAuthorizationAsync(
            targetOrganization, caller.SubjectId, caller.ApiKeyId is null ? null : caller.KeyPermissions, ct);
    }

    public static IResult Fault(AuthorizationFailure failure) => failure switch
    {
        AuthorizationFailure.NotAMember => Problems.NotFound(),
        _ => Problems.Forbidden(),
    };
}
