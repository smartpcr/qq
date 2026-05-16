using System.Collections.Concurrent;

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

/// <summary>
/// In-process implementation of <see cref="ISlackDeadLetterQueue"/> backed
/// by a <see cref="ConcurrentQueue{T}"/>. Intended for development and
/// unit / integration tests; production deployments register the durable
/// implementation supplied by the upstream <c>AgentSwarm.Messaging.Core</c>
/// project against the same <see cref="ISlackDeadLetterQueue"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the dev / test affordance that
/// <see cref="ChannelBasedSlackInboundQueue"/> and
/// <see cref="ChannelBasedSlackOutboundQueue"/> already provide for the
/// inbound / outbound queues, so downstream stages (Stage 4.x retry
/// dispatcher, Stage 5.x outbox dispatcher, e2e scenario 16.2 operator
/// inspection) can integration-test the full poison-message path without
/// having to mock the DLQ contract.
/// </para>
/// <para>
/// Unlike the inbound / outbound adapters, this DLQ is NOT backed by
/// <see cref="System.Threading.Channels.Channel{T}"/>. The DLQ contract
/// requires a snapshot accessor (<see cref="InspectAsync"/>) that returns
/// every currently held entry; <see cref="System.Threading.Channels.Channel{T}"/>
/// is a streaming primitive that does not naturally support inspect-without-drain.
/// <see cref="ConcurrentQueue{T}"/> gives us thread-safe enqueue, FIFO
/// ordering for inspection, and a constant-time snapshot via
/// <see cref="ConcurrentQueue{T}.ToArray"/>.
/// </para>
/// <para>
/// Co-located with <see cref="ISlackDeadLetterQueue"/> in this file because
/// the Stage 1.3 contract and its dev / test stand-in are tightly coupled
/// and both fit comfortably in a single short file; a follow-up may split
/// this into <c>InMemorySlackDeadLetterQueue.cs</c> if the broader
/// codebase convention demands one type per file.
/// </para>
/// </remarks>
internal sealed class InMemorySlackDeadLetterQueue : ISlackDeadLetterQueue
{
    private readonly ConcurrentQueue<SlackDeadLetterEntry> entries = new();

    /// <inheritdoc />
    public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        this.entries.Enqueue(entry);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<SlackDeadLetterEntry> snapshot = this.entries.ToArray();
        return ValueTask.FromResult(snapshot);
    }
}
