namespace Aiep.Application.Execution;

/// <summary>The pricing row in effect for a model, and the cost it implies for a usage (ADR-0009). Prices are USD per
/// 1,000,000 tokens; <see cref="Id"/> is recorded on the run so a cost can be traced to the exact pricing it used.</summary>
public sealed record PricingSnapshot(Guid Id, decimal InputPricePerMtok, decimal OutputPricePerMtok)
{
    public decimal CostFor(int inputTokens, int outputTokens) =>
        (inputTokens / 1_000_000m * InputPricePerMtok) + (outputTokens / 1_000_000m * OutputPricePerMtok);
}

/// <summary>Resolves the versioned pricing for a provider/model (the latest row effective now), or null if none exists.</summary>
public interface IPricing
{
    Task<PricingSnapshot?> ResolveAsync(string provider, string model, CancellationToken cancellationToken = default);
}
