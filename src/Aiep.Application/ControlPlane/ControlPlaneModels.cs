namespace Aiep.Application.ControlPlane;

/// <summary>
/// Who is performing a control-plane action, for audit. Comes from the authenticated principal, never from input.
/// <see cref="ApiKeyId"/> is set only for an API-key caller.
/// </summary>
public sealed record ControlPlaneActor(Guid SubjectId, Guid? ApiKeyId, string? CorrelationId)
{
    /// <summary>The <c>audit_events.actor_type</c> value.</summary>
    public string ActorType => ApiKeyId is null ? "user" : "api_key";
}

public sealed record OrganizationView(Guid Id, string Name, string Status, DateTimeOffset CreatedAt);

public sealed record WorkspaceView(
    Guid Id, string Slug, string Name, string Status, DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt);

public sealed record WorkspaceGrantView(Guid WorkspaceId, Guid SubjectId, string Role);

public sealed record MemberView(
    Guid SubjectId, string OrgRole, string Status, string? DisplayName, string? Email, DateTimeOffset CreatedAt);

/// <summary>Why a member add/change/remove did not proceed. The endpoint maps each to an HTTP status.</summary>
public enum MemberChangeOutcome
{
    Ok,
    SubjectNotFound,
    AlreadyMember,
    NotAMember,

    /// <summary>Only an Owner may grant, change or remove an Owner (authorization-model footnote 2).</summary>
    OwnerProtected,

    /// <summary>The change would leave the organization with no active Owner.</summary>
    LastOwner,
}

public sealed record MemberChangeResult(MemberChangeOutcome Outcome, MemberView? Member)
{
    public static MemberChangeResult Fail(MemberChangeOutcome outcome) => new(outcome, null);
}
