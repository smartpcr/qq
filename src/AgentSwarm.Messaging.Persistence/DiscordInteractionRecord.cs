namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Durable inbox / dedup record for a Discord Gateway interaction. Persisted
/// by <c>DiscordGatewayService</c> <em>before</em> <c>DeferAsync()</c>
/// acknowledges the interaction (architecture.md Section 4.8) so that a crash
/// after Discord considers the interaction delivered does not lose the
/// command. The <see cref="InteractionId"/> primary key plus a UNIQUE
/// constraint on the same column collapse duplicate Gateway redeliveries to a
/// single row -- this is the canonical at-most-once gate per architecture.md
/// Section 3.1.
/// </summary>
/// <remarks>
/// Stored in the shared <c>MessagingDbContext</c> persistence store. The
/// <see cref="RawPayload"/> column carries the full serialized interaction
/// JSON so the pipeline can re-render Discord responses after a crash without
/// any lossy projection at ingest time.
/// </remarks>
public class DiscordInteractionRecord
{
    /// <summary>
    /// Discord interaction snowflake id (unsigned 64-bit). Primary key.
    /// Persisted as a signed <c>long</c> via the SQLite snowflake converter
    /// because Discord snowflakes fit comfortably in 63 bits (the high bit is
    /// a 41-bit timestamp prefix).
    /// </summary>
    public ulong InteractionId { get; set; }

    /// <summary>Shape of the interaction: slash command, button, select menu, or modal submit.</summary>
    public DiscordInteractionType InteractionType { get; set; }

    /// <summary>Discord guild (server) snowflake the interaction originated in.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Discord channel snowflake the interaction was posted in.</summary>
    public ulong ChannelId { get; set; }

    /// <summary>Discord user snowflake of the operator who triggered the interaction.</summary>
    public ulong UserId { get; set; }

    /// <summary>
    /// Full serialized interaction JSON as received from the Gateway. Stored
    /// verbatim so the pipeline (and recovery sweep) can re-derive any field
    /// without joining additional tables. May be large -- callers should
    /// stream-read this column when materializing for recovery.
    /// </summary>
    public string RawPayload { get; set; } = string.Empty;

    /// <summary>First receipt timestamp (set by the gateway service).</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// Set when the pipeline reaches a terminal state for this record
    /// (<see cref="IdempotencyStatus.Completed"/>); <see langword="null"/>
    /// while in flight.
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Lifecycle state. See <see cref="IdempotencyStatus"/>.</summary>
    public IdempotencyStatus IdempotencyStatus { get; set; }

    /// <summary>
    /// Number of times the pipeline has attempted to process this record.
    /// Default 0; incremented by the recovery sweep before each retry.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>Latest failure reason for diagnostics; <see langword="null"/> on success.</summary>
    public string? ErrorDetail { get; set; }
}
