using Aiep.Domain.Runs;

namespace Aiep.Application.Execution;

/// <summary>
/// A single model call. <see cref="Config"/> carries the version's <c>model_config</c> (for example temperature) as
/// opaque string pairs; the fake provider ignores it. Model output is untrusted data (CLAUDE.md, ADR-0007).
/// </summary>
public sealed record ModelRequest(string Model, string Prompt, IReadOnlyDictionary<string, string> Config)
{
    public ModelRequest(string model, string prompt)
        : this(model, prompt, EmptyConfig)
    {
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyConfig =
        new Dictionary<string, string>();
}

/// <summary>The provider's result. Token counts and latency feed <c>task_run_attempts</c> and, later, pricing.</summary>
public sealed record ModelResponse(string Text, int InputTokens, int OutputTokens, TimeSpan Latency);

/// <summary>
/// A model provider behind an adapter (ADR-0009). Phase 2 ships only the deterministic fake; a real Anthropic adapter
/// is a later addition. <see cref="Name"/> matches <c>task_definition_versions.model_provider</c>.
/// </summary>
public interface IModelProvider
{
    string Name { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A provider-side failure carrying its retry classification (and an optional <c>Retry-After</c>). The executor turns
/// this into the run's <see cref="ErrorCategory"/> and backoff decision.
/// </summary>
public sealed class ModelProviderException(ErrorCategory category, string message, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public ErrorCategory Category { get; } = category;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
