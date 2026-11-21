using Aiep.Application.ControlPlane;

namespace Aiep.Application.Execution;

/// <summary>An organization's budget and its committed spend in the current window.</summary>
public sealed record QuotaUsage(bool HasBudget, int WindowSeconds, decimal LimitUsd, decimal CommittedUsd);

/// <summary>Budget administration (threat T12). Enforcement itself happens in the reserve-then-settle ledger at submit
/// and completion.</summary>
public interface IQuota
{
    /// <summary>Sets (or replaces) the organization-wide spend budget.</summary>
    Task SetOrganizationBudgetAsync(
        ControlPlaneActor actor, Guid organizationId, int windowSeconds, decimal limitUsd, CancellationToken cancellationToken = default);

    Task<QuotaUsage> GetOrganizationUsageAsync(
        Guid organizationId, Guid subjectId, CancellationToken cancellationToken = default);
}
