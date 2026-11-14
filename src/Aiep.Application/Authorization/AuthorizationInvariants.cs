namespace Aiep.Application.Authorization;

/// <summary>
/// The delegation and safety rules from the authorization model's <i>Invariants</i> section. These guard administrative
/// actions (granting roles, creating keys, removing owners) and are enforced at the endpoints alongside the permission
/// check. Pure functions so each invariant has a direct unit test.
/// </summary>
public static class AuthorizationInvariants
{
    /// <summary>
    /// "No one can grant a permission they don't hold." True only when every requested permission is in the granter's
    /// effective set. Used for workspace-role grants and for API-key scope (scope ⊆ owner's current permissions).
    /// </summary>
    public static bool CanDelegate(IReadOnlySet<string> granterEffective, IEnumerable<string> requested)
    {
        ArgumentNullException.ThrowIfNull(granterEffective);
        ArgumentNullException.ThrowIfNull(requested);
        return requested.All(granterEffective.Contains);
    }

    /// <summary>
    /// The requested permissions that exceed the ceiling (the owner's current effective permissions). Empty when the
    /// scope is valid. Unknown permission strings are reported too, so a key can't be scoped to a non-existent right.
    /// </summary>
    public static IReadOnlyList<string> ScopeExceeding(IReadOnlySet<string> ownerEffective, IEnumerable<string> requestedScope)
    {
        ArgumentNullException.ThrowIfNull(ownerEffective);
        ArgumentNullException.ThrowIfNull(requestedScope);
        return [.. requestedScope.Where(p => !Permission.IsKnown(p) || !ownerEffective.Contains(p))];
    }

    /// <summary>
    /// "An organization must always have at least one active Owner." True when <paramref name="owner"/> is the only
    /// active owner left, so removing or demoting them must be blocked.
    /// </summary>
    public static bool WouldRemoveLastOwner(IReadOnlyCollection<Guid> activeOwnerSubjectIds, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(activeOwnerSubjectIds);
        return activeOwnerSubjectIds.Contains(owner) && !activeOwnerSubjectIds.Any(id => id != owner);
    }
}
