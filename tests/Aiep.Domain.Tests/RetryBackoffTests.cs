using Aiep.Domain.Runs;

namespace Aiep.Domain.Tests;

public sealed class RetryBackoffTests
{
    private static readonly RetryPolicy Policy =
        new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), MaxAttempts: 3);

    [Theory]
    [InlineData(1, 0.5, 1000)] // ceiling 2000ms  -> 0.5 * 2000
    [InlineData(2, 0.5, 2000)] // ceiling 4000ms
    [InlineData(3, 0.5, 4000)] // ceiling 8000ms
    [InlineData(1, 0.0, 0)]    // full jitter can be ~0
    public void Delay_is_a_fraction_of_the_exponential_ceiling(int attempt, double fraction, int expectedMs)
    {
        var delay = RetryBackoff.Delay(attempt, Policy, fraction);

        Assert.Equal(expectedMs, delay.TotalMilliseconds, 3);
    }

    [Fact]
    public void Delay_is_capped_at_the_policy_maximum()
    {
        // 2s * 2^19 is far beyond the 5-minute cap, so the ceiling is the cap.
        var delay = RetryBackoff.Delay(20, Policy, jitterFraction: 0.5);

        Assert.Equal(Policy.MaxDelay.TotalMilliseconds * 0.5, delay.TotalMilliseconds, 3);
    }

    [Fact]
    public void A_larger_retry_after_wins_over_the_jittered_delay()
    {
        var retryAfter = TimeSpan.FromSeconds(30);

        var delay = RetryBackoff.Delay(1, Policy, jitterFraction: 0.5, retryAfter: retryAfter);

        Assert.Equal(retryAfter, delay);
    }

    [Fact]
    public void A_smaller_retry_after_is_ignored()
    {
        var delay = RetryBackoff.Delay(3, Policy, jitterFraction: 0.5, retryAfter: TimeSpan.FromMilliseconds(100));

        Assert.Equal(4000, delay.TotalMilliseconds, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_attempt_below_one_is_rejected(int attempt) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryBackoff.Delay(attempt, Policy, 0.5));

    [Theory]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    public void A_jitter_fraction_outside_the_unit_interval_is_rejected(double fraction) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryBackoff.Delay(1, Policy, fraction));

    [Fact]
    public void Has_attempts_left_respects_the_policy_maximum()
    {
        Assert.True(Policy.HasAttemptsLeft(2));
        Assert.False(Policy.HasAttemptsLeft(3));
    }
}

public sealed class ErrorCategoryTests
{
    [Theory]
    [InlineData(ErrorCategory.RateLimited)]
    [InlineData(ErrorCategory.Timeout)]
    [InlineData(ErrorCategory.Transient)]
    [InlineData(ErrorCategory.LeaseExpired)]
    public void Retryable_categories_are_retryable(ErrorCategory category) =>
        Assert.True(ErrorCategories.IsRetryable(category));

    [Theory]
    [InlineData(ErrorCategory.Validation)]
    [InlineData(ErrorCategory.Authorization)]
    [InlineData(ErrorCategory.InvalidRequest)]
    [InlineData(ErrorCategory.ContentRefused)]
    [InlineData(ErrorCategory.OutputInvalid)]
    public void Permanent_categories_are_not_retryable(ErrorCategory category) =>
        Assert.False(ErrorCategories.IsRetryable(category));
}
