namespace Aiep.Application.Tenancy;

/// <summary>
/// The tenant scope a unit of work runs under (ADR-0006). Values must come from authentication or from a trusted
/// resource row (for the worker, the run row it claimed), never from a request body or queue payload.
/// The database sees it as <c>app.organization_id</c> and <c>app.subject_id</c>, set for one transaction only.
/// </summary>
public sealed record TenantContext
{
    private TenantContext(Guid? organizationId, Guid? subjectId)
    {
        OrganizationId = organizationId;
        SubjectId = subjectId;
    }

    /// <summary>No tenant and no subject: tenant tables return nothing. For pre-tenant work such as sign-in.</summary>
    public static TenantContext None { get; } = new(null, null);

    public Guid? OrganizationId { get; }

    public Guid? SubjectId { get; }

    /// <summary>An authenticated subject with no organization selected (for example, creating an organization).</summary>
    public static TenantContext ForSubject(Guid subjectId) => new(null, Require(subjectId, nameof(subjectId)));

    /// <summary>
    /// An organization scope. <paramref name="subjectId"/> is the authenticated person, or null for service work
    /// (the worker acting on a claimed run).
    /// </summary>
    public static TenantContext ForOrganization(Guid organizationId, Guid? subjectId) =>
        new(Require(organizationId, nameof(organizationId)),
            subjectId is { } s ? Require(s, nameof(subjectId)) : null);

    private static Guid Require(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Tenant identifiers cannot be empty.", name) : value;
}
