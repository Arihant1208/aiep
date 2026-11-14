namespace Aiep.Application.Authorization;

/// <summary>Why an authorization check failed. Maps to the HTTP status the endpoint returns (ADR-0015).</summary>
public enum AuthorizationFailure
{
    /// <summary>Caller is not an active member of the organization. The endpoint returns <b>404</b> (ADR-0015: a
    /// resource the caller cannot see is hidden, not forbidden).</summary>
    NotAMember,

    /// <summary>Caller is a member but lacks the permission. The endpoint returns <b>403</b> (the resource is visible).</summary>
    MissingPermission,
}

/// <summary>The outcome of one permission check.</summary>
public sealed record AuthorizationResult(bool IsAllowed, AuthorizationFailure? Failure)
{
    public static AuthorizationResult Allowed { get; } = new(true, null);

    public static AuthorizationResult Deny(AuthorizationFailure failure) => new(false, failure);
}

/// <summary>
/// Steps 3–5 of the request algorithm in <c>docs/security/authorization-model.md</c>: given a caller already resolved
/// to one organization (steps 1–2), decide whether a required permission is held. The required permission is always a
/// code constant, never caller input. Steps 6–8 (policies, the tenant-context transaction, audit) surround this and are
/// the endpoint's responsibility.
/// </summary>
public interface IAuthorizationService
{
    AuthorizationResult AuthorizeOrganization(AuthorizationSubject subject, string permission);

    AuthorizationResult AuthorizeWorkspace(AuthorizationSubject subject, Guid workspaceId, string permission);
}

public sealed class AuthorizationService : IAuthorizationService
{
    public AuthorizationResult AuthorizeOrganization(AuthorizationSubject subject, string permission)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Decide(subject, subject.EffectiveAtOrganization(), permission);
    }

    public AuthorizationResult AuthorizeWorkspace(AuthorizationSubject subject, Guid workspaceId, string permission)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Decide(subject, subject.EffectiveInWorkspace(workspaceId), permission);
    }

    private static AuthorizationResult Decide(AuthorizationSubject subject, IReadOnlySet<string> effective, string permission)
    {
        if (!Permission.IsKnown(permission))
        {
            // A required permission comes from a code constant; an unknown one is a bug, not a denial.
            throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        }

        if (!subject.IsActiveMember)
        {
            return AuthorizationResult.Deny(AuthorizationFailure.NotAMember);
        }

        return effective.Contains(permission)
            ? AuthorizationResult.Allowed
            : AuthorizationResult.Deny(AuthorizationFailure.MissingPermission);
    }
}
