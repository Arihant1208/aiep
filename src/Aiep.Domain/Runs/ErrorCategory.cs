using System.Collections.Frozen;

namespace Aiep.Domain.Runs;

/// <summary>
/// Why an execution attempt failed. Classification decides whether the run is retried (ADR-0005,
/// docs/architecture/task-lifecycle.md). The names are also the values stored in
/// <c>task_runs.error_category</c> / <c>task_run_attempts.error_category</c>.
/// </summary>
public enum ErrorCategory
{
    // Retryable: a later attempt may succeed.
    RateLimited,
    Timeout,
    Transient,
    LeaseExpired,

    // Permanent: retrying cannot help, so the run fails immediately.
    Validation,
    Authorization,
    InvalidRequest,
    ContentRefused,
    OutputInvalid,
}

/// <summary>Retry classification for <see cref="ErrorCategory"/>.</summary>
public static class ErrorCategories
{
    private static readonly FrozenSet<ErrorCategory> RetryableSet = new[]
    {
        ErrorCategory.RateLimited,
        ErrorCategory.Timeout,
        ErrorCategory.Transient,
        ErrorCategory.LeaseExpired,
    }.ToFrozenSet();

    /// <summary>True for the only categories the delivery contract allows retrying (task-lifecycle.md).</summary>
    public static bool IsRetryable(ErrorCategory category) => RetryableSet.Contains(category);
}
