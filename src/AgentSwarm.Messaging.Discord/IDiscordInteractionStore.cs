namespace AgentSwarm.Messaging.Discord;

/// <summary>
/// Durable inbox + idempotency tracker for inbound Discord interactions
/// (slash commands, button clicks, select menus, modal submits). The
/// contract is pinned by architecture.md §4.8 and implementation-plan
/// §2.2 line 105:
/// <code>
/// public interface IDiscordInteractionStore
/// {
///     Task&lt;bool&gt; PersistAsync(DiscordInteractionRecord record, CancellationToken ct);
///     Task MarkProcessingAsync(ulong interactionId, CancellationToken ct);
///     Task MarkCompletedAsync(ulong interactionId, CancellationToken ct);
///     Task MarkFailedAsync(ulong interactionId, string errorDetail, CancellationToken ct);
///     Task&lt;IReadOnlyList&lt;DiscordInteractionRecord&gt;&gt; GetRecoverableAsync(int maxRetries, CancellationToken ct);
/// }
/// </code>
/// The interface lives in the <c>AgentSwarm.Messaging.Discord</c> assembly
/// (per architecture.md §4.8 and §6 line 644) alongside the
/// <see cref="DiscordInteractionRecord"/> entity it ferries. The concrete
/// <c>PersistentDiscordInteractionStore</c> implementation lives in
/// <c>AgentSwarm.Messaging.Persistence</c> (per implementation-plan §2.2
/// line 106) so the EF Core <c>MessagingDbContext</c> wiring stays in the
/// shared persistence assembly; Persistence takes a one-way project
/// reference on Discord to see this interface and the entity it returns.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PersistAsync"/> is the canonical dedup boundary: it issues an
/// INSERT and treats a UNIQUE/PK collision (Discord webhook replay,
/// at-least-once Gateway redelivery) as a duplicate -- returning
/// <see langword="false"/> rather than throwing. This is the cross-restart
/// idempotency layer that complements the in-memory
/// <see cref="AgentSwarm.Messaging.Abstractions.IDeduplicationService"/>
/// sliding-window cache (architecture.md §10.2 layered dedup).
/// </para>
/// <para>
/// <see cref="GetRecoverableAsync"/> matches the architecture.md §4.8 / §10.3
/// signature exactly. Implementations may apply a deployment-tuned staleness
/// window (so the sweep does not snatch rows being actively processed by a
/// sibling dispatcher), but the threshold is an implementation detail
/// configured at construction time -- it does not surface on the public
/// contract.
/// </para>
/// </remarks>
public interface IDiscordInteractionStore
{
    /// <summary>
    /// Persists <paramref name="record"/> if its
    /// <see cref="DiscordInteractionRecord.InteractionId"/> has not been
    /// observed before. Returns <see langword="true"/> when the row was
    /// inserted; returns <see langword="false"/> when the UNIQUE constraint
    /// on InteractionId collided (duplicate Discord webhook delivery -- the
    /// inbound pipeline must skip the record entirely).
    /// </summary>
    /// <param name="record">The interaction to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> PersistAsync(DiscordInteractionRecord record, CancellationToken ct);

    /// <summary>
    /// Transitions a previously persisted record from
    /// <see cref="IdempotencyStatus.Received"/> (or
    /// <see cref="IdempotencyStatus.Failed"/> -- retry) into
    /// <see cref="IdempotencyStatus.Processing"/>. Increments
    /// <see cref="DiscordInteractionRecord.AttemptCount"/>. No-op when the
    /// record is already in a terminal state.
    /// </summary>
    /// <param name="interactionId">Discord interaction snowflake.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkProcessingAsync(ulong interactionId, CancellationToken ct);

    /// <summary>
    /// Transitions a record into <see cref="IdempotencyStatus.Completed"/>
    /// and populates <see cref="DiscordInteractionRecord.ProcessedAt"/>.
    /// No-op when the record is missing.
    /// </summary>
    /// <param name="interactionId">Discord interaction snowflake.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkCompletedAsync(ulong interactionId, CancellationToken ct);

    /// <summary>
    /// Transitions a record into <see cref="IdempotencyStatus.Failed"/>,
    /// captures <paramref name="errorDetail"/> on
    /// <see cref="DiscordInteractionRecord.ErrorDetail"/>, and populates
    /// <see cref="DiscordInteractionRecord.ProcessedAt"/>. The record remains
    /// eligible for retry while
    /// <see cref="DiscordInteractionRecord.AttemptCount"/> is below the
    /// configured cap (consulted by <see cref="GetRecoverableAsync"/>).
    /// </summary>
    /// <param name="interactionId">Discord interaction snowflake.</param>
    /// <param name="errorDetail">Failure reason text.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkFailedAsync(ulong interactionId, string errorDetail, CancellationToken ct);

    /// <summary>
    /// Returns every record that the recovery sweep should re-enqueue:
    /// non-Completed status AND
    /// <see cref="DiscordInteractionRecord.AttemptCount"/> strictly less than
    /// <paramref name="maxRetries"/>. Implementations apply an internal
    /// idle-time / lease threshold so the sweep cannot duplicate work that
    /// an active sibling dispatcher is mid-processing; that threshold is a
    /// deployment-tuned ctor parameter on the implementation and is not part
    /// of this public contract (per architecture.md §4.8 signature pin).
    /// </summary>
    /// <param name="maxRetries">
    /// Exclusive upper bound on
    /// <see cref="DiscordInteractionRecord.AttemptCount"/>. A record with
    /// <c>AttemptCount &gt;= maxRetries</c> is treated as permanently failed
    /// and is excluded from the result.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DiscordInteractionRecord>> GetRecoverableAsync(
        int maxRetries,
        CancellationToken ct);
}
