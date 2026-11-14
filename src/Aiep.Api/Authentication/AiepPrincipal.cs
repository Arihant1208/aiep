using System.Globalization;
using System.Security.Claims;
using Aiep.Application.Identity;

namespace Aiep.Api.Authentication;

/// <summary>
/// The only claims an authenticated AIEP request carries. Whatever the IdP put in the token (roles, groups, scopes)
/// is dropped when the principal is built, so it cannot influence authorization (ADR-0008, ADR-0016).
/// </summary>
public static class AiepClaims
{
    public const string SubjectId = "aiep:subject_id";
    public const string AuthenticationMethod = "aiep:auth_method";
    public const string ApiKeyId = "aiep:api_key_id";
    public const string OrganizationId = "aiep:organization_id";
    public const string WorkspaceId = "aiep:workspace_id";
    public const string KeyPermission = "aiep:key_permission";

    // Role checks always fail: AIEP roles live in the database, not in claims.
    private const string NoRoleClaimType = "aiep:roles_are_not_claims";

    public const string MethodOidc = "oidc";
    public const string MethodApiKey = "api_key";

    public static ClaimsPrincipal ForUser(Guid subjectId, string authenticationType) =>
        Build(authenticationType, [
            new Claim(SubjectId, subjectId.ToString()),
            new Claim(AuthenticationMethod, MethodOidc),
        ]);

    public static ClaimsPrincipal ForApiKey(AuthenticatedApiKey key, string authenticationType)
    {
        List<Claim> claims =
        [
            new(SubjectId, key.OwnerSubjectId.ToString()),
            new(AuthenticationMethod, MethodApiKey),
            new(ApiKeyId, key.ApiKeyId.ToString()),
            new(OrganizationId, key.OrganizationId.ToString()),
        ];
        if (key.WorkspaceId is { } workspace)
        {
            claims.Add(new Claim(WorkspaceId, workspace.ToString()));
        }

        claims.AddRange(key.Permissions.Select(p => new Claim(KeyPermission, p)));
        return Build(authenticationType, claims);
    }

    private static ClaimsPrincipal Build(string authenticationType, IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, authenticationType, SubjectId, NoRoleClaimType));
}

/// <summary>Typed view of an authenticated principal built by <see cref="AiepClaims"/>.</summary>
public sealed record CallerIdentity(
    Guid SubjectId,
    string AuthenticationMethod,
    Guid? ApiKeyId,
    Guid? OrganizationId,
    Guid? WorkspaceId,
    IReadOnlyList<string> KeyPermissions)
{
    public static CallerIdentity From(ClaimsPrincipal principal) => new(
        Guid.Parse(principal.FindFirstValue(AiepClaims.SubjectId)
            ?? throw new InvalidOperationException("Principal has no AIEP subject id."), CultureInfo.InvariantCulture),
        principal.FindFirstValue(AiepClaims.AuthenticationMethod) ?? string.Empty,
        ParseOptional(principal.FindFirstValue(AiepClaims.ApiKeyId)),
        ParseOptional(principal.FindFirstValue(AiepClaims.OrganizationId)),
        ParseOptional(principal.FindFirstValue(AiepClaims.WorkspaceId)),
        [.. principal.FindAll(AiepClaims.KeyPermission).Select(c => c.Value)]);

    private static Guid? ParseOptional(string? value) =>
        value is null ? null : Guid.Parse(value, CultureInfo.InvariantCulture);
}
