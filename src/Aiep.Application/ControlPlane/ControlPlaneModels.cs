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
