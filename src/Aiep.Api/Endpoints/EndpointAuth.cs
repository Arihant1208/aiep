using Aiep.Api.Authentication;
using Aiep.Api.Http;
using Aiep.Application.Authorization;
using Aiep.Application.ControlPlane;
using Aiep.Application.Ids;

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

    /// <summary>
    /// Shared prelude for workspace-scoped resources (task definitions, runs): parse the ids, confirm the caller is a
    /// member and the workspace exists and is visible. On failure returns a 404 (concealment). On success returns the
    /// resolved subject and the organization/workspace ids; the caller then gates the specific permission with
    /// <see cref="CheckWorkspace"/>.
    /// </summary>
    public static async Task<(AuthorizationSubject? Subject, Guid Organization, Guid Workspace, IResult? Failure)>
        ResolveWorkspaceAsync(
            string organizationId, string workspaceId, HttpContext http, IControlPlaneStore store, CancellationToken ct)
    {
        if (!ResourceId.TryParse(ResourceId.Organization, organizationId, out var organization)
            || !ResourceId.TryParse(ResourceId.Workspace, workspaceId, out var workspace))
        {
            return (null, default, default, Problems.NotFound());
        }

        var subject = await ResolveAsync(http, organization, store, ct);
        if (subject is null || !subject.IsActiveMember)
        {
            return (null, default, default, Problems.NotFound());
        }

        var found = await store.GetWorkspaceAsync(organization, subject.SubjectId, workspace, ct);
        return found is null
            ? (null, default, default, Problems.NotFound())
            : (subject, organization, workspace, null);
    }

    /// <summary>Whether the caller has any permission in the workspace (so the workspace is visible to them).</summary>
    public static bool CanSeeWorkspace(AuthorizationSubject subject, Guid workspaceId) =>
        subject.EffectiveInWorkspace(workspaceId).Count > 0;

    /// <summary>
    /// Authorize a workspace-scoped permission for a caller already confirmed a member of a workspace that exists.
    /// Returns null when allowed; a 404 when the workspace is invisible to the caller (concealment); a 403 otherwise.
    /// </summary>
    public static IResult? CheckWorkspace(AuthorizationSubject subject, Guid workspaceId, string permission)
    {
        var effective = subject.EffectiveInWorkspace(workspaceId);
        if (effective.Count == 0)
        {
            return Problems.NotFound();
        }

        return effective.Contains(permission) ? null : Problems.Forbidden();
    }
}
