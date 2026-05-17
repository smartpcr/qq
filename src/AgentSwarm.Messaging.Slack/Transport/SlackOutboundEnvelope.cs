using System;

namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Rendered outbound Slack Web API call buffered by
/// <see cref="Queues.ISlackOutboundQueue"/> and consumed by the
/// <c>SlackOutboundDispatcher</c> background service. Carries everything
/// the dispatcher needs to issue the API call without re-rendering the
/// payload.
/// </summary>
/// <remarks>
/// <para>
/// COMPILE STUB introduced by Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. The
/// canonical field surface is owned by Stage 4.1 (Slack Outbound
/// Dispatcher) line 355 of <c>implementation-plan.md</c>, which spells out
/// exactly these fields:
/// <c>TaskId, CorrelationId, MessageType, BlockKitPayload, ThreadTs</c>.
/// Defining the record here lets the queue contracts compile without
/// forcing Stage 4.1 to revise the field list. The brief's
/// <c>MessageType</c> field name is preserved verbatim; its typed value is
/// a <see cref="SlackOutboundOperationKind"/> to avoid colliding with
/// <see cref="AgentSwarm.Messaging.Abstractions.MessageType"/>.
/// </para>
/// <para>
/// Stage 6.3 iter 2 added the optional init-only <see cref="MessageTs"/>
/// and <see cref="ViewId"/> members so producers can carry the
/// <c>chat.update</c> / <c>views.update</c> reference fields without
/// embedding them in the Block Kit payload (the
/// <see cref="Pipeline.SlackOutboundDispatcher"/> still falls back to
/// payload extraction for backward compatibility when these are unset).
/// The primary constructor parameter list is unchanged so all existing
/// <c>SendMessage</c> / <c>SendQuestion</c> call-sites and tests continue
/// to compile.
/// </para>
/// </remarks>
/// <param name="TaskId">Work-item identifier the message belongs to. Used by the dispatcher to resolve the thread mapping and by the audit logger for correlation.</param>
/// <param name="CorrelationId">End-to-end correlation id propagated from agent through messenger and back.</param>
/// <param name="MessageType">Slack Web API verb to invoke (post / update / views.update).</param>
/// <param name="BlockKitPayload">Pre-rendered Block Kit JSON ready to be sent to Slack.</param>
/// <param name="ThreadTs">
/// Slack thread timestamp the message should be posted into. <c>null</c>
/// when the message starts a new top-level post (the dispatcher will create
/// the thread on first send via <c>SlackThreadManager</c>).
/// </param>
internal sealed record SlackOutboundEnvelope(
    string TaskId,
    string CorrelationId,
    SlackOutboundOperationKind MessageType,
    string BlockKitPayload,
    string? ThreadTs)
{
    /// <summary>
    /// Slack message timestamp targeted by a
    /// <see cref="SlackOutboundOperationKind.UpdateMessage"/> envelope.
    /// Required for that verb -- when unset the dispatcher attempts to
    /// extract <c>ts</c> from <see cref="BlockKitPayload"/>; when both
    /// are absent <see cref="Pipeline.HttpClientSlackOutboundDispatchClient"/>
    /// rejects the request as <c>MissingConfiguration</c>. Ignored by
    /// post-message and views.update.
    /// </summary>
    public string? MessageTs { get; init; }

    /// <summary>
    /// Slack view id targeted by a
    /// <see cref="SlackOutboundOperationKind.ViewsUpdate"/> envelope.
    /// Required for that verb -- when unset the dispatcher attempts to
    /// extract <c>view_id</c> (or <c>external_id</c>) from
    /// <see cref="BlockKitPayload"/>; when both are absent the request
    /// is rejected as <c>MissingConfiguration</c>. Ignored by message
    /// verbs.
    /// </summary>
    public string? ViewId { get; init; }

    /// <summary>
    /// Stable per-envelope identifier used by
    /// <see cref="Queues.FileSystemSlackOutboundQueue"/> to name the
    /// journal file and look it up at acknowledgement time. Defaults
    /// to <see cref="Guid.NewGuid"/> on construction so producers that
    /// do not care about the id (every caller other than restart
    /// replay) get a unique value for free. Replay code rehydrates this
    /// from the persisted record so the in-flight file map keeps
    /// pointing at the right journal entry.
    /// </summary>
    public Guid EnvelopeId { get; init; } = Guid.NewGuid();
}
