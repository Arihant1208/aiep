using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aiep.Infrastructure.Execution;

/// <summary>
/// Reads the versioned <c>model_pricing</c> table (ADR-0009). The table is global reference data with no RLS, so this
/// resolves outside any tenant context.
/// </summary>
internal sealed class PricingStore(TenantDatabase database, TimeProvider time) : IPricing
{
    public Task<PricingSnapshot?> ResolveAsync(
        string provider, string model, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.None, async (session, token) =>
        {
            var now = time.GetUtcNow();
            var row = await session.Db.ModelPricing.AsNoTracking()
                .Where(p => p.Provider == provider && p.Model == model && p.EffectiveFrom <= now)
                .OrderByDescending(p => p.EffectiveFrom)
                .FirstOrDefaultAsync(token);
            return row is null ? null : new PricingSnapshot(row.Id, row.InputPricePerMtok, row.OutputPricePerMtok);
        }, cancellationToken);
}
