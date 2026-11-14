namespace Aiep.Domain.Runs;

/// <summary>
/// Retry limits and backoff shape for a task-definition version (defaults from task-lifecycle.md: base 2s, cap 5min,
/// 3 attempts, within a platform maximum).
/// </summary>
public sealed record RetryPolicy(TimeSpan BaseDelay, TimeSpan MaxDelay, int MaxAttempts)
{
    public const int PlatformMaxAttempts = 10;

    public static readonly RetryPolicy Default =
        new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), MaxAttempts: 3);

    /// <summary>Whether another attempt is allowed after <paramref name="attemptsMade"/> have been made.</summary>
    public bool HasAttemptsLeft(int attemptsMade) => attemptsMade < MaxAttempts;
}

/// <summary>
/// Full-jitter exponential backoff (ADR-0005, task-lifecycle.md). The delay before the next attempt is a uniform
/// sample in <c>[0, ceiling]</c> where <c>ceiling = min(cap, base · 2^(attempt-1))</c>, so independent workers do not
/// synchronise their retries. A provider <c>Retry-After</c> is honoured when it is larger.
/// </summary>
public static class RetryBackoff
{
    /// <param name="attempt">The number of the attempt that just failed (>= 1).</param>
    /// <param name="policy">Backoff shape.</param>
    /// <param name="jitterFraction">A sample in [0, 1); inject for tests, else use the <see cref="Random"/> overload.</param>
    /// <param name="retryAfter">A provider-supplied minimum, honoured when larger than the jittered delay.</param>
    public static TimeSpan Delay(int attempt, RetryPolicy policy, double jitterFraction, TimeSpan? retryAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (jitterFraction is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction), jitterFraction, "Must be in [0, 1).");
        }

        var exponential = policy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var ceiling = Math.Min(policy.MaxDelay.TotalMilliseconds, exponential);
        var jittered = TimeSpan.FromMilliseconds(ceiling * jitterFraction);
        return retryAfter is { } after && after > jittered ? after : jittered;
    }

    /// <summary>Backoff with a random jitter sample. Deterministic tests use the explicit-fraction overload.</summary>
    public static TimeSpan Delay(int attempt, RetryPolicy policy, TimeSpan? retryAfter = null) =>
        Delay(attempt, policy, Random.Shared.NextDouble(), retryAfter);
}
