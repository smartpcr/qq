// -----------------------------------------------------------------------
// <copyright file="BufferingAgentTaskServiceDecorator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentTaskService"/> decorator that, after delegating
/// to the inner orchestrator implementation, also enqueues every
/// <see cref="HumanDecisionEvent"/> into an
/// <see cref="ISlackInboundEventBuffer"/> so the Slack
/// <see cref="SlackConnector.ReceiveAsync"/> entry point can drain
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.1. The brief calls for
/// <see cref="SlackConnector.ReceiveAsync"/> to "drain processed
/// inbound events from the inbound pipeline and return as
/// <see cref="System.Collections.Generic.IReadOnlyList{T}"/> of
/// <see cref="MessengerEvent"/>". The Slack inbound pipeline (Stage
/// 5.3 <see cref="SlackInteractionHandler"/>) publishes
/// <see cref="HumanDecisionEvent"/>s through
/// <see cref="IAgentTaskService.PublishDecisionAsync"/>; rather than
/// editing the handler to take a second dependency, the
/// <c>AddSlackMessenger</c> facade decorates whatever
/// <see cref="IAgentTaskService"/> the host registered. The decorator
/// is invisible to the orchestrator and adds at most one queue push
/// per decision.
/// </para>
/// <para>
/// <b>Ordering.</b> The decorator forwards to the inner service
/// FIRST and only enqueues into the buffer after the inner call
/// completes successfully. If the orchestrator throws, the event is
/// NOT buffered -- the inbound handler will roll the dispatch back
/// (or retry) and the event will be republished through the inner
/// orchestrator on the next attempt, where a successful pass will
/// then surface it. This avoids double-counting and keeps the buffer
/// aligned with the durable audit log.
/// </para>
/// <para>
/// <b>Best-effort enqueue.</b> If the buffer is at capacity, the
/// implementation drops an event according to the configured
/// <see cref="AgentSwarm.Messaging.Slack.Configuration.SlackInboundEventBufferOptions.OverflowPolicy"/>
/// (default: drop-newest, see
/// <see cref="InMemorySlackInboundEventBuffer"/>) and increments a
/// drop counter -- the decorator does NOT block or throw on a full
/// buffer because the authoritative copy of every event lives in the
/// durable audit log. A buffer exception is logged and swallowed so
/// the decorator never causes the orchestrator call to fail
/// retrospectively.
/// </para>
/// </remarks>
internal sealed class BufferingAgentTaskServiceDecorator : IAgentTaskService
{
    private readonly IAgentTaskService inner;
    private readonly ISlackInboundEventBuffer buffer;
    private readonly ILogger<BufferingAgentTaskServiceDecorator> logger;

    public BufferingAgentTaskServiceDecorator(
        IAgentTaskService inner,
        ISlackInboundEventBuffer buffer,
        ILogger<BufferingAgentTaskServiceDecorator> logger)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Concrete type of the wrapped inner service. Surfaced for
    /// diagnostic tooling that wants to assert the production
    /// orchestrator (rather than the development NoOp) is what gets
    /// invoked.
    /// </summary>
    public Type InnerServiceType => this.inner.GetType();

    /// <inheritdoc />
    public Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct)
        => this.inner.CreateTaskAsync(request, ct);

    /// <inheritdoc />
    public Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct)
        => this.inner.GetTaskStatusAsync(query, ct);

    /// <inheritdoc />
    public async Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);

        await this.inner.PublishDecisionAsync(decision, ct).ConfigureAwait(false);

        try
        {
            this.buffer.Enqueue(decision);
        }
        catch (Exception ex)
        {
            // Failing to buffer must NOT propagate -- the inner call
            // already succeeded and the durable audit row was written.
            // Log so an operator can correlate the failure with the
            // missing event in a downstream connector poll.
            this.logger.LogWarning(
                ex,
                "BufferingAgentTaskServiceDecorator failed to enqueue HumanDecisionEvent question_id={QuestionId} correlation_id={CorrelationId} into ISlackInboundEventBuffer; SlackConnector.ReceiveAsync will not surface this decision. The durable audit log still contains the event.",
                decision.QuestionId,
                decision.CorrelationId);
        }
    }
}
