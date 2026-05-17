// -----------------------------------------------------------------------
// <copyright file="IAgentTaskService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Orchestrator-facing contract that the messenger connectors depend on to
/// create new agent tasks, query existing task / swarm status, and publish
/// human decisions captured from interactive payloads. Defining the
/// contract in <c>AgentSwarm.Messaging.Abstractions</c> keeps every
/// platform connector (Slack today, Telegram / Discord / Teams tomorrow)
/// decoupled from the orchestrator implementation -- the connector takes
/// an <see cref="IAgentTaskService"/> via DI and never has to know how
/// tasks are actually persisted or dispatched.
/// </summary>
/// <remarks>
/// <para>
/// Introduced by Stage 5.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (implementation step 8: "Define <c>IAgentTaskService</c> interface as
/// the orchestrator-facing contract with methods <c>CreateTaskAsync</c>,
/// <c>GetTaskStatusAsync</c>, <c>PublishDecisionAsync</c> to decouple the
/// Slack connector from orchestrator internals").
/// </para>
/// <para>
/// The interface intentionally uses small request / result records
/// rather than primitive parameters so that future fields (priority,
/// requested agent capability, idempotency hint) can be added without
/// breaking the binary surface every connector compiles against. The
/// records live alongside the interface in this file because their
/// semantics are inseparable from it.
/// </para>
/// <para>
/// Cancellation is named <c>ct</c> (rather than the .NET convention
/// <c>cancellationToken</c>) for source-compatibility with the rest of
/// the messenger contracts already shipped in this assembly
/// (<see cref="IMessengerConnector"/>).
/// </para>
/// </remarks>
public interface IAgentTaskService
{
    /// <summary>
    /// Asks the orchestrator to create a new agent task from a
    /// human-supplied prompt (e.g., the text supplied to
    /// <c>/agent ask ...</c> on Slack). Returns the task's stable
    /// identifier so the connector can open / resume a conversation
    /// thread anchored to it.
    /// </summary>
    /// <param name="request">Prompt text plus the originating messenger
    /// context used by the orchestrator to address its initial reply
    /// (workspace, user, channel, correlation id).</param>
    /// <param name="ct">Cancellation token honouring the inbound
    /// request's lifetime.</param>
    Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct);

    /// <summary>
    /// Queries the orchestrator for the status of a specific task or
    /// the entire swarm. When
    /// <see cref="AgentTaskStatusQuery.TaskId"/> is supplied the
    /// orchestrator returns a single-task snapshot; otherwise it
    /// returns a swarm-wide summary so the requesting human can scan
    /// what every agent is currently working on.
    /// </summary>
    Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct);

    /// <summary>
    /// Publishes a typed <see cref="HumanDecisionEvent"/> produced by
    /// the connector (either from a slash sub-command such as
    /// <c>/agent approve Q-123</c> or from an interactive Block Kit
    /// payload) to the orchestrator's decision pipeline.
    /// </summary>
    Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct);
}

/// <summary>
/// Input bundle for <see cref="IAgentTaskService.CreateTaskAsync"/>.
/// </summary>
/// <param name="Prompt">Raw prompt text the human supplied (e.g., the
/// text after <c>/agent ask </c> on Slack).</param>
/// <param name="Messenger">Source messenger platform ("slack",
/// "telegram", ...) so the orchestrator can address its reply through
/// the right connector.</param>
/// <param name="ExternalUserId">Platform-specific identifier of the
/// human who requested the task (e.g., Slack user id).</param>
/// <param name="ChannelId">Optional platform-specific channel
/// identifier in which the request was raised. Used by the
/// orchestrator to anchor the task thread.</param>
/// <param name="CorrelationId">End-to-end correlation id propagated
/// through the connector pipeline. Connectors typically derive this
/// from the inbound envelope's idempotency key so a Slack retry maps
/// back to the same orchestrator task.</param>
/// <param name="RequestedAt">UTC timestamp at which the connector
/// observed the request.</param>
public sealed record AgentTaskCreationRequest(
    string Prompt,
    string Messenger,
    string ExternalUserId,
    string? ChannelId,
    string CorrelationId,
    DateTimeOffset RequestedAt);

/// <summary>
/// Result of <see cref="IAgentTaskService.CreateTaskAsync"/>.
/// </summary>
/// <param name="TaskId">Stable orchestrator-assigned identifier for the
/// new task. Connectors use it to anchor a conversation thread.</param>
/// <param name="CorrelationId">Correlation id echoed back from the
/// supplied <see cref="AgentTaskCreationRequest"/>. The orchestrator
/// MAY substitute a different correlation id (e.g., if it merges the
/// request into an existing task); the connector treats whatever it
/// receives here as authoritative.</param>
/// <param name="Acknowledgement">Short human-readable confirmation
/// the connector can echo back to the requesting user (e.g.,
/// "Task T-42 created -- watch this thread for updates."). May be
/// empty when the orchestrator has nothing meaningful to say beyond
/// the task id.</param>
public sealed record AgentTaskCreationResult(
    string TaskId,
    string CorrelationId,
    string Acknowledgement);

/// <summary>
/// Input bundle for <see cref="IAgentTaskService.GetTaskStatusAsync"/>.
/// </summary>
/// <param name="TaskId">Optional task identifier to query. When
/// <see langword="null"/> or empty the orchestrator returns a
/// swarm-wide snapshot.</param>
/// <param name="Messenger">Source messenger platform issuing the
/// query.</param>
/// <param name="CorrelationId">Correlation id for audit / tracing.</param>
public sealed record AgentTaskStatusQuery(
    string? TaskId,
    string Messenger,
    string CorrelationId);

/// <summary>
/// Result of <see cref="IAgentTaskService.GetTaskStatusAsync"/>.
/// Lightweight, presentation-agnostic shape that connectors render
/// into the appropriate native format (Block Kit on Slack, message
/// entities on Telegram, embeds on Discord, ...).
/// </summary>
/// <param name="Scope">Either <c>"task"</c> (single-task query) or
/// <c>"swarm"</c> (no task id supplied). Allows the renderer to pick
/// the right headline.</param>
/// <param name="Summary">Single-line summary the connector can render
/// as the headline of an ephemeral reply.</param>
/// <param name="Entries">Per-task status rows. May be empty when the
/// requested task does not exist or the swarm is idle.</param>
public sealed record AgentTaskStatusResult(
    string Scope,
    string Summary,
    IReadOnlyList<AgentTaskStatusEntry> Entries);

/// <summary>
/// One row of an <see cref="AgentTaskStatusResult"/>: a task's
/// identifier, status string, and human-readable description.
/// </summary>
public sealed record AgentTaskStatusEntry(
    string TaskId,
    string Status,
    string Description);
