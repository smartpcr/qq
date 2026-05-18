// -----------------------------------------------------------------------
// <copyright file="SlackConnector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Slack implementation of the platform-neutral
/// <see cref="IMessengerConnector"/>. <see cref="SendMessageAsync"/>
/// and <see cref="SendQuestionAsync"/> render the payload via
/// <see cref="ISlackMessageRenderer"/>, resolve (or create) the per-task
/// thread via <see cref="ISlackThreadManager"/>, wrap the result in a
/// <see cref="SlackOutboundEnvelope"/>, enqueue it on
/// <see cref="ISlackOutboundQueue"/> for the
/// <see cref="SlackOutboundDispatcher"/> background service to deliver,
/// and write a connector-level <c>message_send</c> audit row through
/// <see cref="ISlackAuditLogger"/>. <see cref="ReceiveAsync"/> drains
/// processed inbound events from the connector-internal
/// <see cref="ISlackInboundEventBuffer"/> populated by
/// <see cref="BufferingAgentTaskServiceDecorator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 8.1 step 1 explicitly requires the
/// connector to "compose all internal components: inbound transport,
/// ingestor, outbound dispatcher, thread manager, audit logger". The
/// constructor takes a <see cref="SlackConnectorComponents"/> bundle
/// rather than 5+ individual interfaces so the composition is
/// explicit at compile time AND the constructor signature is stable
/// against future additions. The hosted-service lifecycle peers
/// (<see cref="SlackInboundIngestor"/> +
/// <see cref="SlackOutboundDispatcher"/>) are validated at
/// facade-build time by
/// <c>SlackMessengerServiceCollectionExtensions.ValidateConnectorHostedServiceComposition</c>
/// so a missing registration surfaces synchronously inside
/// <c>AddSlackMessenger</c> instead of as a dropped message at runtime.
/// </para>
/// <para>
/// The connector NEVER dispatches to the Slack Web API directly --
/// enqueuing through the shared outbound queue is what lets the
/// dispatcher's token-bucket rate limiter (Tier 2 for
/// <c>chat.postMessage</c>) cap the per-channel send rate even when
/// the upstream orchestrator produces a burst.
/// </para>
/// <para>
/// Stage 8.1: <see cref="ReceiveAsync"/> drains processed inbound
/// events from <see cref="ISlackInboundEventBuffer"/>, which is fed
/// by <see cref="BufferingAgentTaskServiceDecorator"/> -- the
/// decorator that <c>AddSlackMessenger</c> wraps around the host's
/// <see cref="IAgentTaskService"/>. The buffer is bounded but the
/// authoritative copy of every event lives in the durable audit log
/// (<c>slack_audit_entry</c>); the default overflow policy is
/// <see cref="SlackInboundEventBufferOverflowPolicy.DropNewest"/> so
/// the OLDEST events are preserved and a stalled poller eventually
/// drains them once it catches up (see
/// <see cref="InMemorySlackInboundEventBuffer"/>).
/// </para>
/// <para>
/// <b>Connector-level audit row.</b> Every successful enqueue writes
/// a <c>message_send</c> audit row through the
/// <see cref="ISlackAuditLogger"/> bundled in
/// <see cref="SlackConnectorComponents"/>. This row is independent of
/// (and complementary to) the per-HTTP-call row the
/// <see cref="SlackOutboundDispatcher"/> writes after the actual
/// <c>chat.postMessage</c>; together they give operators a complete
/// trace from "orchestrator asked the connector to send" through
/// "Slack accepted the message". Audit failures are swallowed (logged
/// only) so a transient audit-write blip never breaks the send.
/// </para>
/// <para>
/// <b>Clock source.</b> The audit row's <see cref="SlackAuditEntry.Timestamp"/>
/// is stamped from the injected <see cref="TimeProvider"/> -- the same
/// clock <c>AddMessagingCore</c> registers (via <c>TryAddSingleton</c>)
/// so a test composition root that swaps in a fake time source
/// (e.g., <c>FakeTimeProvider</c>) observes deterministic timestamps
/// on connector-emitted audit rows, matching the rest of the Slack
/// pipeline (e.g., <see cref="SlackOutboundDispatcher"/>). Calling
/// <see cref="DateTimeOffset.UtcNow"/> here would silently bypass the
/// fake and defeat the testability guarantee.
/// </para>
/// </remarks>
internal sealed class SlackConnector : IMessengerConnector
{
    /// <summary>
    /// Maximum number of buffered events a single
    /// <see cref="ReceiveAsync"/> poll returns. Keeps an aggressive
    /// poller from monopolising the buffer (and the resulting
    /// downstream processing) while still draining enough events per
    /// call to make steady progress under typical throughput.
    /// </summary>
    internal const int DefaultReceiveBatchSize = 256;

    /// <summary>
    /// Constant <see cref="SlackAuditEntry.RequestType"/> used for
    /// connector-level outbound audit rows (one row per
    /// <see cref="SendMessageAsync"/> /
    /// <see cref="SendQuestionAsync"/> call).
    /// </summary>
    internal const string ConnectorSendRequestType = "message_send";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly IReadOnlyList<MessengerEvent> EmptyEventList = Array.Empty<MessengerEvent>();

    private readonly SlackConnectorComponents components;
    private readonly ISlackMessageRenderer renderer;
    private readonly IOptionsMonitor<SlackOutboundOptions> outboundOptions;
    private readonly ILogger<SlackConnector> logger;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new <see cref="SlackConnector"/> using the
    /// canonical wall-clock <see cref="TimeProvider.System"/>. This
    /// is the constructor invoked by direct <c>new</c> callers (e.g.,
    /// tests that exercise the connector without a DI container);
    /// production wiring always goes through DI, which prefers the
    /// internal 5-argument constructor below so a DI-registered
    /// override (real clock today, fake clock under test) propagates
    /// to every <see cref="SlackAuditEntry.Timestamp"/> emitted here.
    /// </summary>
    public SlackConnector(
        SlackConnectorComponents components,
        ISlackMessageRenderer renderer,
        IOptionsMonitor<SlackOutboundOptions> outboundOptions,
        ILogger<SlackConnector> logger)
        : this(
            components,
            renderer,
            outboundOptions,
            logger,
            TimeProvider.System)
    {
    }

    /// <summary>
    /// Full-dependency constructor preferred by
    /// <c>Microsoft.Extensions.DependencyInjection</c> (which selects
    /// the ctor with the most resolvable parameters). The Stage 8.1
    /// composition root registers <see cref="TimeProvider"/> via
    /// <c>AddMessagingCore</c> as a <c>TryAddSingleton</c>, so a test
    /// fixture that pre-registers a fake <see cref="TimeProvider"/>
    /// (e.g., <c>FakeTimeProvider</c>) sees its instance flow into
    /// every <see cref="WriteConnectorSendAuditAsync"/> call -- the
    /// guarantee that motivated bundling the connector behind DI in
    /// the first place.
    /// </summary>
    internal SlackConnector(
        SlackConnectorComponents components,
        ISlackMessageRenderer renderer,
        IOptionsMonitor<SlackOutboundOptions> outboundOptions,
        ILogger<SlackConnector> logger,
        TimeProvider timeProvider)
    {
        this.components = components ?? throw new ArgumentNullException(nameof(components));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.outboundOptions = outboundOptions ?? throw new ArgumentNullException(nameof(outboundOptions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        string teamId = this.ResolveTeamId();
        SlackThreadMapping mapping = await this.components.ThreadManager
            .GetOrCreateThreadAsync(message.TaskId, message.AgentId, message.CorrelationId, teamId, ct)
            .ConfigureAwait(false);

        object renderedBlocks = this.renderer.RenderMessage(message);
        string payload = JsonSerializer.Serialize(renderedBlocks, PayloadJsonOptions);

        SlackOutboundEnvelope envelope = new(
            TaskId: message.TaskId,
            CorrelationId: message.CorrelationId,
            MessageType: SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: payload,
            ThreadTs: mapping.ThreadTs);

        await this.components.OutboundTransport.EnqueueAsync(envelope).ConfigureAwait(false);

        await this.WriteConnectorSendAuditAsync(
            taskId: message.TaskId,
            agentId: message.AgentId,
            correlationId: message.CorrelationId,
            mapping: mapping,
            payload: payload,
            ct: ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackConnector enqueued message task_id={TaskId} correlation_id={CorrelationId} thread_ts={ThreadTs}.",
            message.TaskId,
            message.CorrelationId,
            mapping.ThreadTs);
    }

    /// <inheritdoc />
    public async Task SendQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        ct.ThrowIfCancellationRequested();

        string teamId = this.ResolveTeamId();
        SlackThreadMapping mapping = await this.components.ThreadManager
            .GetOrCreateThreadAsync(question.TaskId, question.AgentId, question.CorrelationId, teamId, ct)
            .ConfigureAwait(false);

        object renderedBlocks = this.renderer.RenderQuestion(question);
        string payload = JsonSerializer.Serialize(renderedBlocks, PayloadJsonOptions);

        SlackOutboundEnvelope envelope = new(
            TaskId: question.TaskId,
            CorrelationId: question.CorrelationId,
            MessageType: SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: payload,
            ThreadTs: mapping.ThreadTs);

        await this.components.OutboundTransport.EnqueueAsync(envelope).ConfigureAwait(false);

        await this.WriteConnectorSendAuditAsync(
            taskId: question.TaskId,
            agentId: question.AgentId,
            correlationId: question.CorrelationId,
            mapping: mapping,
            payload: payload,
            ct: ct).ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackConnector enqueued question task_id={TaskId} question_id={QuestionId} correlation_id={CorrelationId} thread_ts={ThreadTs}.",
            question.TaskId,
            question.QuestionId,
            question.CorrelationId,
            mapping.ThreadTs);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct)
    {
        // Stage 8.1: drain processed inbound events from the
        // connector-internal buffer that BufferingAgentTaskServiceDecorator
        // populates. The contract is non-blocking, so an empty buffer
        // returns an empty list rather than awaiting new events.
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<MessengerEvent> drained = this.components.InboundEventBuffer.Drain(DefaultReceiveBatchSize);
        if (drained.Count == 0)
        {
            return Task.FromResult(EmptyEventList);
        }

        this.logger.LogDebug(
            "SlackConnector.ReceiveAsync drained {EventCount} buffered MessengerEvent(s); buffer remaining={Remaining} dropped_total={Dropped}.",
            drained.Count,
            this.components.InboundEventBuffer.Count,
            this.components.InboundEventBuffer.Dropped);

        return Task.FromResult(drained);
    }

    private string ResolveTeamId()
    {
        string? teamId = this.outboundOptions.CurrentValue?.DefaultTeamId;
        if (string.IsNullOrWhiteSpace(teamId))
        {
            throw new InvalidOperationException(
                "Slack:Outbound:DefaultTeamId is not configured. The connector requires a workspace id to bind outbound messages to.");
        }

        return teamId;
    }

    private async Task WriteConnectorSendAuditAsync(
        string taskId,
        string agentId,
        string correlationId,
        SlackThreadMapping mapping,
        string payload,
        CancellationToken ct)
    {
        SlackAuditEntry entry = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId ?? string.Empty,
            AgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId,
            TaskId = string.IsNullOrWhiteSpace(taskId) ? null : taskId,
            ConversationId = mapping.ThreadTs,
            Direction = "outbound",
            RequestType = ConnectorSendRequestType,
            TeamId = mapping.TeamId,
            ChannelId = mapping.ChannelId,
            ThreadTs = mapping.ThreadTs,
            MessageTs = null,
            UserId = null,
            CommandText = null,
            ResponsePayload = payload,
            Outcome = "success",
            ErrorDetail = null,
            Timestamp = this.timeProvider.GetUtcNow(),
        };

        try
        {
            await this.components.AuditLogger.LogAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Failing to write the connector-level audit row must NOT
            // propagate -- the envelope is already on the outbound
            // queue and the dispatcher will produce its own per-HTTP
            // audit row when chat.postMessage returns. Log so an
            // operator can correlate the gap.
            this.logger.LogWarning(
                ex,
                "SlackConnector failed to write {RequestType} audit row task_id={TaskId} correlation_id={CorrelationId} thread_ts={ThreadTs}; the outbound envelope is still enqueued and the dispatcher's per-HTTP audit row will capture the delivery outcome.",
                ConnectorSendRequestType,
                taskId,
                correlationId,
                mapping.ThreadTs);
        }
    }
}
