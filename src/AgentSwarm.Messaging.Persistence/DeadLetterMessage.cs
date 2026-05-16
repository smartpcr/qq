using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Terminal-state record for an <see cref="OutboundMessage"/> that exhausted
/// its <c>MaxAttempts</c> retry budget without success. Created by the
/// outbound queue when it transitions a message to
/// <see cref="OutboundMessageStatus.DeadLettered"/> (architecture.md Section
/// 3.1 DeadLetterMessage; relationship <c>1--1</c> with the originating
/// <see cref="OutboundMessage"/> via <see cref="OriginalMessageId"/>).
/// </summary>
public class DeadLetterMessage
{
    /// <summary>
    /// Surrogate primary key. Client-assigned at construction via the
    /// property initializer (<see cref="Guid.NewGuid"/>) so freshly created
    /// instances never collide on <see cref="Guid.Empty"/>. EF is configured
    /// with <c>ValueGeneratedNever</c> to respect this client value rather
    /// than overwrite it; SQLite has no built-in Guid generator so server-
    /// side assignment is not an option.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// <see cref="OutboundMessage.MessageId"/> of the message that failed.
    /// Foreign key into <see cref="OutboundMessage"/> with a UNIQUE index,
    /// which together enforce the <c>1--1</c> relationship from architecture.md
    /// Section 3.2: every dead-letter row points to exactly one outbound row,
    /// and repeated dead-letter calls for the same outbound message collide
    /// on the unique index rather than accumulating duplicate rows.
    /// </summary>
    public Guid OriginalMessageId { get; set; }

    /// <summary>
    /// Connector-native channel identifier (Discord channel snowflake cast
    /// to <c>long</c>) carried over from the original message for triage
    /// without joining back to <see cref="OutboundMessage"/>.
    /// </summary>
    public long ChatId { get; set; }

    /// <summary>Original rendered payload (Discord embed JSON, etc.).</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Last failure reason captured before dead-lettering.</summary>
    public string ErrorReason { get; set; } = string.Empty;

    /// <summary>When the message was moved to the dead-letter store.</summary>
    public DateTimeOffset FailedAt { get; set; }

    /// <summary>
    /// Number of dispatch attempts that had been made before dead-lettering.
    /// Carried over from <see cref="OutboundMessage.AttemptCount"/> at the
    /// moment of transition.
    /// </summary>
    public int AttemptCount { get; set; }
}
