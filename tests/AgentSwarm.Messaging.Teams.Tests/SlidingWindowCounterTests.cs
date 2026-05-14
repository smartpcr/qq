using AgentSwarm.Messaging.Teams.Middleware;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class SlidingWindowCounterTests
{
    [Fact]
    public void TryAcquire_AllowsRequests_BelowLimit()
    {
        var counter = new SlidingWindowCounter(limit: 3, window: TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;

        Assert.True(counter.TryAcquire(now, out _));
        Assert.True(counter.TryAcquire(now, out _));
        Assert.True(counter.TryAcquire(now, out _));
        Assert.Equal(3, counter.Count);
    }

    [Fact]
    public void TryAcquire_RejectsRequest_OverLimit()
    {
        var counter = new SlidingWindowCounter(limit: 2, window: TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;
        Assert.True(counter.TryAcquire(now, out _));
        Assert.True(counter.TryAcquire(now, out _));

        var acquired = counter.TryAcquire(now, out var retryAfter);

        Assert.False(acquired);
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(retryAfter <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void TryAcquire_AllowsAfterWindowSlides()
    {
        var counter = new SlidingWindowCounter(limit: 1, window: TimeSpan.FromMinutes(1));
        var t0 = DateTimeOffset.UtcNow;
        Assert.True(counter.TryAcquire(t0, out _));
        Assert.False(counter.TryAcquire(t0, out _));

        var ok = counter.TryAcquire(t0.Add(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1))), out _);

        Assert.True(ok);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public void Ctor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowCounter(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowCounter(1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowCounter(1, TimeSpan.FromMinutes(-1)));
    }
}
