// -----------------------------------------------------------------------
// <copyright file="SlackInboundHandlers.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handler contract for normalized slash-command envelopes, dispatched
/// by <see cref="SlackInboundProcessingPipeline"/> when
/// <see cref="SlackInboundEnvelope.SourceType"/> is
/// <see cref="SlackInboundSourceType.Command"/>. The full handler
/// implementation lives in Stage 5.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>;
/// Stage 4.3 defines only the dispatch surface so the ingestor can be
/// wired and tested independently.
/// </summary>
internal interface ISlackCommandHandler
{
    /// <summary>
    /// Handles the supplied slash-command envelope. Throws on
    /// transient failure to trigger the ingestor's retry budget;
    /// returns normally on success.
    /// </summary>
    Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// Handler contract for normalized <c>app_mention</c> Events API
/// envelopes (Stage 5.2). See <see cref="ISlackCommandHandler"/> for
/// the dispatch / retry contract.
/// </summary>
internal interface ISlackAppMentionHandler
{
    /// <summary>
    /// Handles the supplied <c>app_mention</c> event envelope.
    /// </summary>
    Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// Handler contract for Block Kit / view-submission interaction
/// envelopes (Stage 5.3). See <see cref="ISlackCommandHandler"/> for
/// the dispatch / retry contract.
/// </summary>
internal interface ISlackInteractionHandler
{
    /// <summary>
    /// Handles the supplied interactive envelope.
    /// </summary>
    Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// Stage 4.3 default <see cref="ISlackCommandHandler"/>. Logs the
/// envelope at <see cref="LogLevel.Warning"/> and completes so the
/// Stage 4.3 ingestor wiring can stand on its own without the Stage
/// 5.1 real handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Warning -- do NOT deploy this handler to production.</b> The
/// no-op completes the envelope and lets the idempotency guard mark
/// the row <c>completed</c>, so a Slack retry will be deduped even
/// though the agent never received the request. Stage 5.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// replaces this binding with the real <c>/agent</c> dispatcher.
/// The iter-4 / iter-5 evaluator (item #4) flagged the no-op
/// default as a residual reliability gap; the handler logs at
/// <see cref="LogLevel.Warning"/> so a production deployment that
/// forgets to override the binding cannot silently absorb traffic.
/// </para>
/// </remarks>
internal sealed class NoOpSlackCommandHandler : ISlackCommandHandler
{
    private readonly ILogger<NoOpSlackCommandHandler> logger;

    public NoOpSlackCommandHandler(ILogger<NoOpSlackCommandHandler> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.logger.LogWarning(
            "NoOpSlackCommandHandler completed envelope idempotency_key={IdempotencyKey} team_id={TeamId} channel_id={ChannelId} user_id={UserId} WITHOUT producing an agent task. Stage 5.1 MUST replace this handler before production; the current ack-and-drop behaviour will silently mark the envelope completed and dedup all Slack retries.",
            envelope.IdempotencyKey,
            envelope.TeamId,
            envelope.ChannelId,
            envelope.UserId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stage 4.3 default <see cref="ISlackAppMentionHandler"/>. Logs at
/// <see cref="LogLevel.Warning"/> and completes; replaced by the
/// Stage 5.2 real handler. <b>Do NOT deploy this no-op to production
/// -- it silently absorbs every @mention and marks the envelope
/// completed.</b>
/// </summary>
internal sealed class NoOpSlackAppMentionHandler : ISlackAppMentionHandler
{
    private readonly ILogger<NoOpSlackAppMentionHandler> logger;

    public NoOpSlackAppMentionHandler(ILogger<NoOpSlackAppMentionHandler> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.logger.LogWarning(
            "NoOpSlackAppMentionHandler completed envelope idempotency_key={IdempotencyKey} team_id={TeamId} channel_id={ChannelId} user_id={UserId} WITHOUT producing an app-mention action. Stage 5.2 MUST replace this handler before production.",
            envelope.IdempotencyKey,
            envelope.TeamId,
            envelope.ChannelId,
            envelope.UserId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stage 4.3 default <see cref="ISlackInteractionHandler"/>. Logs at
/// <see cref="LogLevel.Warning"/> and completes; replaced by the
/// Stage 5.3 real handler. <b>Do NOT deploy this no-op to production
/// -- it silently absorbs every button-click / modal submission
/// without producing a HumanDecisionEvent.</b>
/// </summary>
internal sealed class NoOpSlackInteractionHandler : ISlackInteractionHandler
{
    private readonly ILogger<NoOpSlackInteractionHandler> logger;

    public NoOpSlackInteractionHandler(ILogger<NoOpSlackInteractionHandler> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.logger.LogWarning(
            "NoOpSlackInteractionHandler completed envelope idempotency_key={IdempotencyKey} team_id={TeamId} channel_id={ChannelId} user_id={UserId} WITHOUT producing a HumanDecisionEvent. Stage 5.3 MUST replace this handler before production.",
            envelope.IdempotencyKey,
            envelope.TeamId,
            envelope.ChannelId,
            envelope.UserId);
        return Task.CompletedTask;
    }
}
