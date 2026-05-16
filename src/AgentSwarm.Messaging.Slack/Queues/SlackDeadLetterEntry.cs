namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// A poison message captured by <see cref="ISlackDeadLetterQueue"/> after
/// it exceeded the configured retry budget. The original
/// <see cref="Transport.SlackInboundEnvelope"/> or
/// <see cref="Transport.SlackOutboundEnvelope"/> is preserved via
/// <see cref="Payload"/> and discriminated by <see cref="Source"/> so that
/// the operator inspection tool (see e2e scenario 16.2) can render it
/// alongside the failure context.
/// </summary>
/// <remarks>
/// COMPILE STUB introduced by Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. The
/// brief lists <c>EnqueueAsync</c> + <c>InspectAsync</c> on the DLQ
/// interface but leaves the entry shape to the implementer; the fields
/// below are the minimum needed to satisfy e2e scenario 16.2 ("all 3
/// poison messages are returned with their original payload and failure
/// details").
/// </remarks>
/// <param name="EntryId">
/// Stable identifier for the DLQ entry (ULID-style). Allows operators to
/// reference a specific entry when replaying.
/// </param>
/// <param name="Source">Which queue the poison message came from.</param>
/// <param name="Payload">
/// The original envelope (boxed because the DLQ contract is non-generic so
/// it can hold both inbound and outbound payloads -- the brief calls out
/// a single <see cref="ISlackDeadLetterQueue"/> rather than two).
/// </param>
/// <param name="FailureReason">Short human-readable reason category.</param>
/// <param name="AttemptCount">Number of processing attempts before dead-lettering.</param>
/// <param name="DeadLetteredAt">UTC timestamp at which the entry was moved to the DLQ.</param>
/// <param name="ExceptionType">Fully-qualified type name of the final exception, or <c>null</c> if the failure was non-exception (e.g., HTTP 5xx after retries).</param>
/// <param name="ExceptionMessage">Final exception <see cref="Exception.Message"/>, or <c>null</c>.</param>
internal sealed record SlackDeadLetterEntry(
    string EntryId,
    SlackDeadLetterSource Source,
    object Payload,
    string FailureReason,
    int AttemptCount,
    DateTimeOffset DeadLetteredAt,
    string? ExceptionType,
    string? ExceptionMessage);
