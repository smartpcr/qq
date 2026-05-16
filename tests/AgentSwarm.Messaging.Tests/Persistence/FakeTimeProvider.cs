namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Manually-advanced <see cref="TimeProvider"/> for deterministic tests of
/// time-sensitive store behaviour (dedup TTL, retry backoff, expiry sweeps).
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public DateTimeOffset UtcNow => _now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public void SetUtcNow(DateTimeOffset value) => _now = value;
}
