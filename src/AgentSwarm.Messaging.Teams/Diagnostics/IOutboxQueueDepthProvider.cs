namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Read-only depth probe consumed by the <see cref="TeamsConnectorTelemetry"/>
/// <c>teams.outbox.queue_depth</c> observable gauge. The contract is intentionally
/// synchronous and cheap to call — the OpenTelemetry exporter invokes the gauge on
/// every collection cycle (typically every second) and a blocking I/O call here would
/// stall the exporter pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Production hosts wire an implementation that returns the last value cached by the
/// outbox engine (e.g. set after each <c>DequeueAsync</c> tick in
/// <c>OutboxRetryEngine.ProcessOnceAsync</c>). Test hosts and hosts that have not
/// composed the outbox engine yet wire <see cref="NullOutboxQueueDepthProvider.Instance"/>
/// so the gauge reports zero without throwing.
/// </para>
/// <para>
/// The interface lives in the <c>AgentSwarm.Messaging.Teams.Diagnostics</c> namespace
/// rather than <c>AgentSwarm.Messaging.Core</c> because the gauge instrument name
/// (<c>teams.outbox.queue_depth</c>) is Teams-specific per
/// <c>implementation-plan.md</c> §6.3. A future per-messenger gauge would add a sibling
/// interface rather than overloading this one.
/// </para>
/// </remarks>
public interface IOutboxQueueDepthProvider
{
    /// <summary>
    /// Return the last observed queue depth (number of entries in
    /// <c>OutboxEntryStatuses.Pending</c>). Implementations MUST NOT block on I/O and
    /// MUST NOT throw — return 0 when the value is unknown.
    /// </summary>
    long GetQueueDepth();
}

/// <summary>
/// No-op <see cref="IOutboxQueueDepthProvider"/> that always reports <c>0</c>. Used by
/// hosts that have not yet wired the outbox engine and by unit tests that exercise the
/// telemetry surface without a real queue.
/// </summary>
public sealed class NullOutboxQueueDepthProvider : IOutboxQueueDepthProvider
{
    /// <summary>Shared instance.</summary>
    public static readonly NullOutboxQueueDepthProvider Instance = new();

    private NullOutboxQueueDepthProvider()
    {
    }

    /// <inheritdoc />
    public long GetQueueDepth() => 0;
}

/// <summary>
/// Thread-safe in-memory <see cref="IOutboxQueueDepthProvider"/> backed by a single
/// 64-bit counter updated via <see cref="System.Threading.Interlocked.Exchange(ref long, long)"/>.
/// Production composition: the outbox engine pushes <see cref="SetQueueDepth"/> after
/// each <c>DequeueAsync</c> tick; the gauge reader polls <see cref="GetQueueDepth"/>.
/// </summary>
public sealed class InMemoryOutboxQueueDepthProvider : IOutboxQueueDepthProvider
{
    private long _depth;

    /// <summary>
    /// Update the last observed depth. Idempotent; the most recent caller wins per
    /// <see cref="System.Threading.Interlocked.Exchange(ref long, long)"/> semantics.
    /// </summary>
    public void SetQueueDepth(long value) => Interlocked.Exchange(ref _depth, value);

    /// <inheritdoc />
    public long GetQueueDepth() => Interlocked.Read(ref _depth);
}

/// <summary>
/// Stage 6.3 (iter-2) — bridges the depth observation that
/// <see cref="AgentSwarm.Messaging.Core.OutboxRetryEngine"/> already maintains on
/// <see cref="AgentSwarm.Messaging.Core.OutboxMetrics"/> onto the Teams-side
/// <c>teams.outbox.queue_depth</c> gauge. Production composition resolves this
/// provider as the default when <see cref="AgentSwarm.Messaging.Core.OutboxMetrics"/>
/// is present in DI (see
/// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddTeamsConnectorTelemetry"/>),
/// so a single push (<c>OutboxRetryEngine.ProcessOnceAsync</c>'s call to
/// <see cref="AgentSwarm.Messaging.Core.OutboxMetrics.SetPendingCount"/>) feeds both
/// the existing <c>teams.outbox.pending_count</c> gauge and the §6.3
/// <c>teams.outbox.queue_depth</c> gauge — no double bookkeeping required.
/// </summary>
public sealed class OutboxMetricsQueueDepthProvider : IOutboxQueueDepthProvider
{
    private readonly AgentSwarm.Messaging.Core.OutboxMetrics _metrics;

    /// <summary>Construct the provider from the canonical outbox metrics singleton.</summary>
    /// <exception cref="ArgumentNullException">If <paramref name="metrics"/> is null.</exception>
    public OutboxMetricsQueueDepthProvider(AgentSwarm.Messaging.Core.OutboxMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <inheritdoc />
    public long GetQueueDepth() => _metrics.GetPendingCount();
}
