// -----------------------------------------------------------------------
// <copyright file="NoOpAgentTaskService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Development-only <see cref="IAgentTaskService"/> stand-in: logs every
/// orchestrator call and returns synthetic results so the Slack
/// connector can be exercised end-to-end before the real orchestrator
/// project lands.
/// </summary>
/// <remarks>
/// <para>
/// Stage 5.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The brief defines <see cref="IAgentTaskService"/> as the
/// "orchestrator-facing contract"; the orchestrator implementation is
/// out of scope for this story. The Worker host wires this no-op so
/// the dispatcher chain resolves cleanly while the orchestrator is
/// built upstream; production composition roots replace the binding
/// with the real service BEFORE calling
/// <see cref="SlackCommandDispatchServiceCollectionExtensions.AddSlackCommandDispatcher"/>.
/// </para>
/// <para>
/// <b>Do NOT deploy this stub to production.</b> The
/// <see cref="CreateTaskAsync"/> path returns a deterministic stub
/// task id derived from the correlation id so audit logs and the
/// Stage 6.2 thread manager have a stable identifier to anchor to,
/// but no real agent task is created and no real decision is
/// dispatched -- the swarm never observes the request.
/// </para>
/// </remarks>
internal sealed class NoOpAgentTaskService : IAgentTaskService
{
    private readonly ILogger<NoOpAgentTaskService> logger;

    public NoOpAgentTaskService(ILogger<NoOpAgentTaskService> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string taskId = "stub-" + (string.IsNullOrEmpty(request.CorrelationId)
            ? Guid.NewGuid().ToString("N").Substring(0, 8)
            : request.CorrelationId);

        this.logger.LogWarning(
            "NoOpAgentTaskService.CreateTaskAsync produced stub task task_id={TaskId} correlation_id={CorrelationId} messenger={Messenger} prompt_length={PromptLength}; no real agent task was created. Stage 5.1+ MUST replace this stub before production.",
            taskId,
            request.CorrelationId,
            request.Messenger,
            request.Prompt?.Length ?? 0);

        return Task.FromResult(new AgentTaskCreationResult(
            TaskId: taskId,
            CorrelationId: request.CorrelationId,
            Acknowledgement: $"Stub task `{taskId}` registered (development orchestrator -- no real agent dispatched)."));
    }

    /// <inheritdoc />
    public Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        this.logger.LogWarning(
            "NoOpAgentTaskService.GetTaskStatusAsync returning empty stub snapshot for task_id={TaskId} correlation_id={CorrelationId}; no real orchestrator query was made.",
            query.TaskId,
            query.CorrelationId);

        string scope = string.IsNullOrEmpty(query.TaskId) ? "swarm" : "task";
        return Task.FromResult(new AgentTaskStatusResult(
            Scope: scope,
            Summary: scope == "task"
                ? $"No status available for task `{query.TaskId}` (development orchestrator stub)."
                : "No agent tasks in flight (development orchestrator stub).",
            Entries: Array.Empty<AgentTaskStatusEntry>()));
    }

    /// <inheritdoc />
    public Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);

        this.logger.LogWarning(
            "NoOpAgentTaskService.PublishDecisionAsync absorbed decision question_id={QuestionId} action={ActionValue} messenger={Messenger} correlation_id={CorrelationId}; no real orchestrator received the event.",
            decision.QuestionId,
            decision.ActionValue,
            decision.Messenger,
            decision.CorrelationId);

        return Task.CompletedTask;
    }
}
