namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Hand-rolled deterministic <see cref="TimeProvider"/> mirroring the EF-Core test
/// fixture variant. Defaults to a fixed UTC offset; tests can call
/// <see cref="Advance"/> to simulate time passing without wall-clock sleeps.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
