namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Persistence boundary for <see cref="PendingDisambiguation"/> records
/// captured by the Stage 2.2 inbound pipeline when a command resolves to
/// multiple <c>OperatorBinding</c> rows. Stage 3.3 consumes the stored
/// entry to re-issue the original command bound to the
/// operator-selected workspace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stub vs production.</b> The Stage 2.2 stub
/// <c>InMemoryPendingDisambiguationStore</c> is a process-local
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>;
/// the Stage 5.3 production replacement is a row-per-entry table on the
/// shared persistence store. Both honour the same single-use semantics
/// (<see cref="TakeAsync"/> removes-on-read) so a replayed callback
/// cannot re-trigger the original command.
/// </para>
/// <para>
/// <b>Atomicity.</b> <see cref="TakeAsync"/> MUST be a single atomic
/// remove-on-read — a probe-then-remove pair would let two concurrent
/// callbacks for the same token both succeed and double-issue the
/// original command. Implementations on top of EF Core SHOULD use
/// <c>SELECT ... FOR UPDATE; DELETE</c> in a single transaction.
/// </para>
/// </remarks>
public interface IPendingDisambiguationStore
{
    /// <summary>
    /// Persist a freshly-emitted disambiguation prompt. Implementations
    /// must reject a duplicate <see cref="PendingDisambiguation.Token"/>
    /// (the pipeline guarantees collision-free generation, so a duplicate
    /// here indicates a tooling bug worth surfacing).
    /// </summary>
    Task StoreAsync(PendingDisambiguation entry, CancellationToken ct);

    /// <summary>
    /// Atomically read AND remove the entry keyed by <paramref name="token"/>.
    /// Returns <c>null</c> when the token is unknown OR when the entry has
    /// already expired (per <see cref="PendingDisambiguation.ExpiresAt"/>).
    /// </summary>
    Task<PendingDisambiguation?> TakeAsync(string token, CancellationToken ct);

    /// <summary>
    /// Drop every entry whose <see cref="PendingDisambiguation.ExpiresAt"/>
    /// is in the past relative to <paramref name="now"/>. Background
    /// sweeper hook; the in-memory stub is also safe to call ad-hoc.
    /// </summary>
    Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct);
}
