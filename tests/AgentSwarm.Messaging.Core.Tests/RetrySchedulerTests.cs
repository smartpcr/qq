namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Pins the Stage 6.1 retry policy numbers
/// (base 2s, ×2 exponential, ±25% jitter, 60s cap, Retry-After honoured).
/// </summary>
public sealed class RetrySchedulerTests
{
    private static OutboxOptions DefaultOptions() => new()
    {
        BaseBackoffSeconds = 2.0,
        MaxBackoffSeconds = 60.0,
        JitterRatio = 0.25,
    };

    [Theory]
    [InlineData(1, 2.0)]
    [InlineData(2, 4.0)]
    [InlineData(3, 8.0)]
    [InlineData(4, 16.0)]
    [InlineData(5, 32.0)]
    public void ComputeDelay_ExponentialBaseSequence_IsCorrect(int attempt, double expectedBaseSeconds)
    {
        var options = DefaultOptions();
        var delay = RetryScheduler.ComputeDelay(attempt, options, random: new Random(42));

        var min = expectedBaseSeconds * (1.0 - options.JitterRatio);
        var max = expectedBaseSeconds * (1.0 + options.JitterRatio);

        Assert.InRange(delay.TotalSeconds, min, max);
    }

    [Fact]
    public void ComputeDelay_AppliesMaxBackoffCeiling()
    {
        var options = DefaultOptions();

        // attempt = 7 → base = 2 * 64 = 128 s, well above the 60 s cap.
        var delay = RetryScheduler.ComputeDelay(attempt: 7, options, random: new Random(42));

        Assert.True(delay.TotalSeconds <= options.MaxBackoffSeconds);
    }

    [Fact]
    public void ComputeDelay_JitterBoundsAreRespected()
    {
        var options = DefaultOptions();
        var rng = new Random(1234);

        for (var i = 0; i < 200; i++)
        {
            var delay = RetryScheduler.ComputeDelay(attempt: 2, options, rng);
            // attempt 2 → base 4 s, ±25% → [3, 5] s
            Assert.InRange(delay.TotalSeconds, 3.0, 5.0);
        }
    }

    [Fact]
    public void ComputeDelay_ThrowsOnNonPositiveAttempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetryScheduler.ComputeDelay(attempt: 0, DefaultOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetryScheduler.ComputeDelay(attempt: -3, DefaultOptions()));
    }

    [Fact]
    public void ComputeDelay_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(
            () => RetryScheduler.ComputeDelay(attempt: 1, options: null!));
    }

    [Fact]
    public void NextRetryAt_HonoursServerRetryAfterWhenItExceedsComputedBackoff()
    {
        var options = DefaultOptions();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var retryAfter = TimeSpan.FromSeconds(45);

        // attempt 1 → base 2s (well below the 45s Retry-After hint)
        var next = RetryScheduler.NextRetryAt(attempt: 1, options, retryAfter, clock, random: new Random(0));

        Assert.Equal(clock.GetUtcNow().Add(retryAfter), next);
    }

    [Fact]
    public void NextRetryAt_PrefersComputedBackoffWhenItExceedsRetryAfter()
    {
        var options = DefaultOptions();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        // attempt 5 → base 32 s, RetryAfter 1 s → computed wins.
        var next = RetryScheduler.NextRetryAt(attempt: 5, options, TimeSpan.FromSeconds(1), clock, random: new Random(0));

        Assert.True((next - clock.GetUtcNow()).TotalSeconds > 1.0);
    }

    [Fact]
    public void NextRetryAt_ThrowsOnNullClock()
    {
        Assert.Throws<ArgumentNullException>(
            () => RetryScheduler.NextRetryAt(attempt: 1, DefaultOptions(), retryAfter: null, clock: null!));
    }
}
