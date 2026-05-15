namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Hand-rolled deterministic <see cref="TimeProvider"/> for tests that assert on
/// <c>DeactivatedAt</c> / <c>UpdatedAt</c> timestamps. Defaults to a fixed UTC offset; tests
/// can call <see cref="Advance"/> to simulate time passing without relying on
/// <see cref="System.Diagnostics.Stopwatch"/> or wall-clock sleeps.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
