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
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 6.3 Slack implementation of the platform-neutral
/// <see cref="IMessengerConnector"/>. <see cref="SendMessageAsync"/>
/// and <see cref="SendQuestionAsync"/> render the payload via
/// <see cref="ISlackMessageRenderer"/>, resolve (or create) the per-task
/// thread via <see cref="ISlackThreadManager"/>, wrap the result in a
/// <see cref="SlackOutboundEnvelope"/>, and enqueue it on
/// <see cref="ISlackOutboundQueue"/> for the
/// <see cref="SlackOutboundDispatcher"/> background service to deliver.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 6.3 steps 3 + 4. The connector NEVER
/// dispatches to the Slack Web API directly -- enqueuing through the
/// shared outbound queue is what lets the dispatcher's token-bucket
/// rate limiter (Tier 2 for <c>chat.postMessage</c>) cap the per-channel
/// send rate even when the upstream orchestrator produces a burst.
/// </para>
/// <para>
/// <see cref="ReceiveAsync"/> is a deliberate no-op for Stage 6.3:
/// Slack's inbound path already publishes typed
/// <see cref="HumanDecisionEvent"/> instances directly through
/// <see cref="Pipeline.SlackInteractionHandler"/> (Stage 5.3), so there
/// is no second seam to drain here. The contract returns an empty list
/// rather than throwing so a host that polls every registered connector
/// uniformly does not have to special-case Slack.
/// </para>
/// </remarks>
internal sealed class SlackConnector : IMessengerConnector
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly IReadOnlyList<MessengerEvent> EmptyEventList = Array.Empty<MessengerEvent>();

    private readonly ISlackOutboundQueue outboundQueue;
    private readonly ISlackThreadManager threadManager;
    private readonly ISlackMessageRenderer renderer;
    private readonly IOptionsMonitor<SlackOutboundOptions> outboundOptions;
    private readonly ILogger<SlackConnector> logger;

    public SlackConnector(
        ISlackOutboundQueue outboundQueue,
        ISlackThreadManager threadManager,
        ISlackMessageRenderer renderer,
        IOptionsMonitor<SlackOutboundOptions> outboundOptions,
        ILogger<SlackConnector> logger)
    {
        this.outboundQueue = outboundQueue ?? throw new ArgumentNullException(nameof(outboundQueue));
        this.threadManager = threadManager ?? throw new ArgumentNullException(nameof(threadManager));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.outboundOptions = outboundOptions ?? throw new ArgumentNullException(nameof(outboundOptions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        string teamId = this.ResolveTeamId();
        SlackThreadMapping mapping = await this.threadManager
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

        await this.outboundQueue.EnqueueAsync(envelope).ConfigureAwait(false);

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
        SlackThreadMapping mapping = await this.threadManager
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

        await this.outboundQueue.EnqueueAsync(envelope).ConfigureAwait(false);

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
        // No-op: inbound Slack interactions publish HumanDecisionEvents
        // directly via SlackInteractionHandler (Stage 5.3). Hosts that
        // poll every connector uniformly receive an empty list rather
        // than NotImplementedException.
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(EmptyEventList);
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
}
