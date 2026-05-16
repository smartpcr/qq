namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// Captures inbound and outbound Slack envelopes that exhausted the
/// configured retry budget (see <see cref="Retry.ISlackRetryPolicy"/>).
/// Operators can later <see cref="InspectAsync"/> the DLQ to triage and
/// replay poison messages, per e2e scenario 16.2 in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/e2e-scenarios.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 1.3 contract.</b> The brief lists a single non-generic DLQ
/// interface rather than separate inbound / outbound DLQs -- both
/// pipelines feed the same dead-letter surface, with the source
/// discriminated by <see cref="SlackDeadLetterEntry.Source"/>.
/// </para>
/// <para>
/// In production the swap-in implementation will be a durable DLQ
/// (database-backed table, Service Bus dead-letter sub-queue, etc.)
/// supplied by the upstream <c>AgentSwarm.Messaging.Core</c> project per
/// Stage 1.3 of <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </para>
/// </remarks>
internal interface ISlackDeadLetterQueue
{
    /// <summary>
    /// Captures a poison message. Implementations MUST persist the entry
    /// before completing the returned task so it survives connector
    /// restart (durable production implementations) or process exit
    /// (in-memory dev implementations make no such guarantee).
    /// </summary>
    ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns all currently held poison messages so operators (or
    /// integration tests) can triage them. Implementations MUST return an
    /// immutable snapshot -- mutating the underlying store while the
    /// caller iterates is not permitted.
    /// </summary>
    ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default);
}
