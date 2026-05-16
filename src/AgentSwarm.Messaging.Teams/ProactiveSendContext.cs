namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Ambient (<see cref="System.Threading.AsyncLocal{T}"/>) envelope carrying the
/// <c>OutboxEntryId</c> of the pending <c>IMessageOutbox</c> entry that is being
/// dispatched. Stage 5.1 iter-4 evaluator feedback item 2 — the
/// <see cref="Security.InstallationStateGate"/> needs the outbox entry ID so it can call
/// <c>IMessageOutbox.DeadLetterAsync</c> on a rejected send, but the public proactive-send
/// surface (<see cref="AgentSwarm.Messaging.Abstractions.IProactiveNotifier"/> /
/// <see cref="AgentSwarm.Messaging.Abstractions.IMessengerConnector"/>) intentionally does
/// NOT carry an outbox entry ID parameter — the contracts are platform-agnostic and the
/// outbox is a Phase 6 implementation detail.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the Phase 6 outbox engine uses this.</b> When the outbox engine leases an entry
/// and is about to dispatch it via <c>IProactiveNotifier</c> /
/// <c>IMessengerConnector</c>, it wraps the call in
/// <c>using (ProactiveSendContext.WithOutboxEntryId(entry.OutboxEntryId)) { ... }</c>.
/// The notifier / connector then read <see cref="CurrentOutboxEntryId"/> and pass it to
/// <see cref="Security.InstallationStateGate.CheckAsync"/> /
/// <see cref="Security.InstallationStateGate.CheckTargetAsync"/>. When the gate rejects,
/// the entry is dead-lettered automatically. When the gate is invoked OUTSIDE an outbox
/// dispatch (synchronous tests, direct callers), <see cref="CurrentOutboxEntryId"/>
/// returns <c>null</c> and the gate skips the dead-letter call but still emits the
/// <c>InstallationGateRejected</c> audit row.
/// </para>
/// <para>
/// <b>Why AsyncLocal and not a parameter.</b> Adding an <c>outboxEntryId</c> parameter to
/// every <see cref="AgentSwarm.Messaging.Abstractions.IProactiveNotifier"/> method would
/// (a) leak an outbox-implementation concern into the platform-agnostic contract,
/// (b) require every downstream messenger (Slack, Discord, Telegram) to thread the value
/// through despite having no use for it, and (c) break every existing call site /
/// test. The AsyncLocal envelope keeps the contract unchanged and lets the Phase 6
/// outbox engine opt in to dead-letter wiring without coupling the gate to a specific
/// outbox shape.
/// </para>
/// <para>
/// <b>Thread / async safety.</b> Values flow with the logical async call chain just like
/// <c>HttpContext.Current</c> or <c>ActivitySource.Current</c>. Nested
/// <see cref="WithOutboxEntryId"/> calls form a stack — disposing the returned scope
/// restores the prior value (not <c>null</c>). The implementation snapshots the previous
/// value on entry and restores it on dispose, so concurrent dispatch loops on different
/// async paths see independent values.
/// </para>
/// </remarks>
public static class ProactiveSendContext
{
    private static readonly System.Threading.AsyncLocal<string?> _outboxEntryId = new();

    /// <summary>
    /// The outbox entry ID for the proactive send currently being dispatched on this
    /// logical async call chain, or <c>null</c> when the send was initiated outside an
    /// outbox dispatch (e.g. from a synchronous test or a direct
    /// <see cref="AgentSwarm.Messaging.Abstractions.IProactiveNotifier"/> caller).
    /// </summary>
    public static string? CurrentOutboxEntryId => _outboxEntryId.Value;

    /// <summary>
    /// Begin a scope in which <see cref="CurrentOutboxEntryId"/> returns
    /// <paramref name="outboxEntryId"/>. The caller MUST dispose the returned scope to
    /// restore the prior value. Intended for use by the Phase 6 outbox engine around its
    /// <c>IProactiveNotifier</c> / <c>IMessengerConnector</c> dispatch calls.
    /// </summary>
    /// <param name="outboxEntryId">The outbox entry ID to flow to the gate; must be non-empty.</param>
    /// <returns>A disposable scope that restores the previous value on dispose.</returns>
    /// <exception cref="ArgumentException">If <paramref name="outboxEntryId"/> is null or empty.</exception>
    public static IDisposable WithOutboxEntryId(string outboxEntryId)
    {
        if (string.IsNullOrEmpty(outboxEntryId))
        {
            throw new ArgumentException("Outbox entry ID is required.", nameof(outboxEntryId));
        }

        var previous = _outboxEntryId.Value;
        _outboxEntryId.Value = outboxEntryId;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private int _disposed;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _outboxEntryId.Value = _previous;
            }
        }
    }
}
