namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Validates <see cref="TokenBucketRateLimiter"/> against the canonical 50 msg/sec
/// ceiling under a deterministic clock.
/// </summary>
public sealed class TokenBucketRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_ConsumesInitialBurstWithoutWaiting()
    {
        var options = new OutboxOptions { RateLimitPerSecond = 50, RateLimitBurst = 5 };
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var limiter = new TokenBucketRateLimiter(options, clock);

        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(CancellationToken.None);
        }

        // All 5 initial burst tokens should have been consumed without advancing the clock.
        Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
    }

    [Fact]
    public void Constructor_RejectsNonPositiveRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketRateLimiter(new OutboxOptions { RateLimitPerSecond = 0, RateLimitBurst = 1 }));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBurst()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketRateLimiter(new OutboxOptions { RateLimitPerSecond = 50, RateLimitBurst = 0 }));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new TokenBucketRateLimiter(null!));
    }

    [Fact]
    public async Task AcquireAsync_HonoursCancellation()
    {
        // Configure a very slow refill so the limiter will need to await Task.Delay.
        var options = new OutboxOptions { RateLimitPerSecond = 1, RateLimitBurst = 1 };
        var limiter = new TokenBucketRateLimiter(options);

        // Consume the only token.
        await limiter.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.AcquireAsync(cts.Token));
    }
}
