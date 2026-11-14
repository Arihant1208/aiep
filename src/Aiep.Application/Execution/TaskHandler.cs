using Aiep.Domain.Runs;

namespace Aiep.Application.Execution;

/// <summary>
/// Everything a handler needs to execute one attempt, read from the run and its task-definition version. All strings
/// are raw JSON / text; the handler owns their interpretation. <see cref="InputJson"/> is caller-supplied and untrusted.
/// </summary>
public sealed record HandlerInput(
    string HandlerType,
    string HandlerVersion,
    string ModelProvider,
    string ModelName,
    string ModelConfig,
    string? PromptTemplate,
    string InputJson,
    string OutputSchema);

/// <summary>A successful attempt: the output JSON to persist, and the usage to record on the attempt.</summary>
public sealed record HandlerResult(string OutputJson, ModelUsage Usage);

public sealed record ModelUsage(int InputTokens, int OutputTokens, TimeSpan ProviderLatency);

/// <summary>
/// A trusted, platform-authored task handler (ADR-0007: no customer code). Resolved from the allowlist by
/// (<see cref="HandlerType"/>, <see cref="Version"/>), which match <c>task_definition_versions.handler_type</c> and
/// <c>handler_version</c>.
/// </summary>
public interface ITaskHandler
{
    string HandlerType { get; }

    string Version { get; }

    Task<HandlerResult> ExecuteAsync(HandlerInput input, CancellationToken cancellationToken);
}

/// <summary>
/// A permanent, handler-side failure (bad input, invalid output). Retryable provider failures are
/// <see cref="ModelProviderException"/> instead.
/// </summary>
public sealed class HandlerException(ErrorCategory category, string message) : Exception(message)
{
    public ErrorCategory Category { get; } = category;
}

/// <summary>The allowlist of handlers the platform will run (ADR-0007).</summary>
public interface IHandlerRegistry
{
    /// <summary>The matching handler, or <c>null</c> when none is allowlisted for that type and version.</summary>
    ITaskHandler? Resolve(string handlerType, string version);
}
