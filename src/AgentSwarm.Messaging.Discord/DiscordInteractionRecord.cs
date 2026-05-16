namespace AgentSwarm.Messaging.Discord;

/// <summary>
/// Persistent record of an inbound Discord interaction (slash command, button,
/// select menu, modal submit). Inbox-side idempotency anchor for the Discord
/// connector: <see cref="InteractionId"/> is the primary key with a companion
/// UNIQUE index, so duplicate webhook replays from Discord collide at the
/// constraint level rather than re-running the pipeline (architecture.md
/// §4.8 and §10.2).
/// </summary>
/// <remarks>
/// <para>
/// All Discord snowflake columns are stored as signed <c>INTEGER</c> via the
/// <c>MessagingDbContext</c> SnowflakeConverter; the
/// <c>ulong &lt;-&gt; long</c> reinterpretation is loss-free for every
/// Discord-issued id (snowflakes occupy the lower 63 bits).
/// </para>
/// <para>
/// The lifecycle is:
/// <c>Received -&gt; Processing -&gt; Completed</c> on the happy path;
/// <c>... -&gt; Failed -&gt; Processing -&gt; ...</c> on retry. The
/// <c>InteractionRecoverySweep</c> (Stage 3.5) scans Received/Processing/Failed
/// rows older than the recovery threshold via
/// <see cref="IDiscordInteractionStore.GetRecoverableAsync"/>.
/// </para>
/// <para>
/// The entity lives in the Discord assembly (alongside
/// <see cref="IDiscordInteractionStore"/>) so the pinned interface contract
/// in architecture.md §4.8 can compile without forcing
/// <c>AgentSwarm.Messaging.Discord</c> to take a project reference on
/// <c>AgentSwarm.Messaging.Persistence</c>. The
/// <c>PersistentDiscordInteractionStore</c> implementation and the
/// <c>MessagingDbContext</c> EF configuration both live in Persistence and
/// reach the entity through a one-way Persistence -&gt; Discord project
/// reference.
/// </para>
/// </remarks>
public class DiscordInteractionRecord
{
    /// <summary>
    /// Discord's interaction snowflake -- the inbound idempotency anchor.
    /// Primary key with a UNIQUE companion index; duplicate webhook replays
    /// collide at the constraint level.
    /// </summary>
    public ulong InteractionId { get; set; }

    /// <summary>
    /// Discord interaction kind. Mirrors the four shapes the Gateway delivers
    /// (slash command, button click, select menu, modal submit).
    /// </summary>
    public DiscordInteractionType InteractionType { get; set; }

    /// <summary>Discord guild (server) snowflake. Required for tenant validation.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Discord channel snowflake.</summary>
    public ulong ChannelId { get; set; }

    /// <summary>Discord user snowflake of the human who triggered the interaction.</summary>
    public ulong UserId { get; set; }

    /// <summary>
    /// Raw interaction payload (JSON serialised from
    /// <c>Discord.WebSocket.SocketInteraction</c>) retained for replay and audit
    /// (FR-006). Required; use <c>"{}"</c> when no extras apply.
    /// </summary>
    public string RawPayload { get; set; } = string.Empty;

    /// <summary>UTC instant the interaction was first persisted.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// UTC instant processing reached a terminal state
    /// (<see cref="IdempotencyStatus.Completed"/> or
    /// <see cref="IdempotencyStatus.Failed"/>).
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>
    /// Lifecycle state used to drive idempotent processing and restart
    /// recovery.
    /// </summary>
    public IdempotencyStatus IdempotencyStatus { get; set; } = IdempotencyStatus.Received;

    /// <summary>
    /// Number of times processing has been attempted. Incremented on each
    /// transition through <see cref="IDiscordInteractionStore.MarkProcessingAsync"/>;
    /// the recovery sweep skips rows whose count has reached the configured cap.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Failure detail when <see cref="IdempotencyStatus"/> is
    /// <see cref="IdempotencyStatus.Failed"/>. Carries the most recent error
    /// text, suitable for operator triage.
    /// </summary>
    public string? ErrorDetail { get; set; }
}
