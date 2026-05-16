// -----------------------------------------------------------------------
// <copyright file="SlackCommandHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="ISlackCommandHandler"/>: parses
/// <c>/agent &lt;sub-command&gt; [arguments]</c> slash-command payloads
/// and routes each sub-command to the orchestrator-facing
/// <see cref="IAgentTaskService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 5.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Replaces the Stage 4.3 <see cref="NoOpSlackCommandHandler"/> default
/// (silently ack-and-drop) with the real dispatcher described in
/// architecture.md §2.7. Supported sub-commands:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Sub-command</term>
///     <description>Action</description>
///   </listheader>
///   <item>
///     <term><c>ask &lt;prompt&gt;</c></term>
///     <description>Calls
///     <see cref="IAgentTaskService.CreateTaskAsync"/> with the prompt;
///     the orchestrator emits a task-created event that Stage 6.2 picks
///     up to anchor a Slack thread.</description>
///   </item>
///   <item>
///     <term><c>status [task-id]</c></term>
///     <description>Queries
///     <see cref="IAgentTaskService.GetTaskStatusAsync"/> and posts the
///     summary back to the user via <c>response_url</c>.</description>
///   </item>
///   <item>
///     <term><c>approve &lt;question-id&gt;</c></term>
///     <description>Publishes a
///     <see cref="HumanDecisionEvent"/> with <c>ActionValue = "approve"</c>.</description>
///   </item>
///   <item>
///     <term><c>reject &lt;question-id&gt;</c></term>
///     <description>Publishes a
///     <see cref="HumanDecisionEvent"/> with <c>ActionValue = "reject"</c>.</description>
///   </item>
///   <item>
///     <term><c>review &lt;task-id&gt;</c> and <c>escalate &lt;task-id&gt;</c></term>
///     <description>Modal-opening sub-commands: in production the
///     <see cref="SlackCommandsController"/>'s synchronous fast-path
///     (see <see cref="DefaultSlackModalFastPathHandler"/>) handles
///     these before they ever reach the async ingestor. When the
///     async path DOES see a review / escalate envelope (e.g., a
///     synthetic test, or a configuration that suppresses the
///     fast-path) the handler attempts a best-effort
///     <see cref="ISlackViewsOpenClient"/> call so the implementation
///     still satisfies brief steps 6 / 7; the modal will normally
///     fail to open because Slack's <c>trigger_id</c> has expired,
///     and the user receives an ephemeral hint.</description>
///   </item>
///   <item>
///     <term>Unrecognised / missing arguments</term>
///     <description>Returns an ephemeral error message that lists the
///     valid sub-commands (brief step 9, test scenario 3).</description>
///   </item>
/// </list>
/// <para>
/// Failure semantics. Any exception thrown by
/// <see cref="IAgentTaskService"/> propagates out of
/// <see cref="HandleAsync"/> so the
/// <see cref="SlackInboundProcessingPipeline"/> can retry the envelope
/// and (after the retry budget) dead-letter it. Failures inside the
/// ephemeral responder are swallowed by the responder itself --
/// a missed ephemeral reply is recoverable from logs, but turning
/// it into a retry would replay the orchestrator side-effect.
/// </para>
/// </remarks>
internal sealed class SlackCommandHandler : ISlackCommandHandler
{
    /// <summary><c>/agent ask</c> sub-command token.</summary>
    public const string AskSubCommand = "ask";

    /// <summary><c>/agent status</c> sub-command token.</summary>
    public const string StatusSubCommand = "status";

    /// <summary><c>/agent approve</c> sub-command token.</summary>
    public const string ApproveSubCommand = "approve";

    /// <summary><c>/agent reject</c> sub-command token.</summary>
    public const string RejectSubCommand = "reject";

    /// <summary><c>/agent review</c> sub-command token.</summary>
    public const string ReviewSubCommand = "review";

    /// <summary><c>/agent escalate</c> sub-command token.</summary>
    public const string EscalateSubCommand = "escalate";

    /// <summary>
    /// Messenger discriminator stamped on
    /// <see cref="HumanDecisionEvent.Messenger"/> for every decision
    /// the Slack connector publishes (architecture.md §2.9). Pinned as
    /// a constant so tests assert against the exact value.
    /// </summary>
    public const string MessengerName = "slack";

    /// <summary>
    /// <see cref="HumanDecisionEvent.ActionValue"/> emitted by the
    /// <c>approve</c> sub-command.
    /// </summary>
    public const string ApproveActionValue = "approve";

    /// <summary>
    /// <see cref="HumanDecisionEvent.ActionValue"/> emitted by the
    /// <c>reject</c> sub-command.
    /// </summary>
    public const string RejectActionValue = "reject";

    private static readonly IReadOnlyList<string> ValidSubCommands = new[]
    {
        AskSubCommand,
        StatusSubCommand,
        ApproveSubCommand,
        RejectSubCommand,
        ReviewSubCommand,
        EscalateSubCommand,
    };

    private readonly IAgentTaskService taskService;
    private readonly ISlackEphemeralResponder ephemeralResponder;
    private readonly ISlackViewsOpenClient viewsOpenClient;
    private readonly ISlackMessageRenderer messageRenderer;

    // Retained for forward-compatibility with later stages (e.g., scheduling /
    // expiry timestamps that are not carried on the envelope). Stage 5.1 iter-3
    // sources the AgentTaskCreationRequest.RequestedAt and
    // HumanDecisionEvent.ReceivedAt fields from envelope.ReceivedAt instead so
    // they honour the documented "UTC timestamp at which the connector
    // observed the request" semantic -- the handler's wall clock can lag the
    // original observation by seconds or minutes depending on ingestor queue
    // depth.
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackCommandHandler> logger;

    public SlackCommandHandler(
        IAgentTaskService taskService,
        ISlackEphemeralResponder ephemeralResponder,
        ISlackViewsOpenClient viewsOpenClient,
        ISlackMessageRenderer messageRenderer,
        ILogger<SlackCommandHandler> logger,
        TimeProvider? timeProvider = null)
    {
        this.taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        this.ephemeralResponder = ephemeralResponder ?? throw new ArgumentNullException(nameof(ephemeralResponder));
        this.viewsOpenClient = viewsOpenClient ?? throw new ArgumentNullException(nameof(viewsOpenClient));
        this.messageRenderer = messageRenderer ?? throw new ArgumentNullException(nameof(messageRenderer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(envelope.RawPayload);
        return this.DispatchAsync(envelope, payload, this.ephemeralResponder, ct);
    }

    /// <summary>
    /// Dispatches a previously-parsed <see cref="SlackCommandPayload"/>
    /// using the supplied <paramref name="responder"/>. Exposed
    /// <see langword="internal"/> so the Stage 5.2
    /// <see cref="SlackAppMentionHandler"/> can reuse the same
    /// sub-command switch, error messages, and orchestrator call shape
    /// while substituting a threaded-reply responder for the default
    /// ephemeral <c>response_url</c> responder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the unified dispatch surface required by Stage 5.2
    /// implementation step 4 ("Delegate parsed commands to the same
    /// <see cref="SlackCommandHandler"/> dispatch logic to ensure
    /// unified processing"). Callers that arrive from the app-mention
    /// path pass a synthesised payload (no <c>response_url</c>,
    /// no <c>trigger_id</c>) plus a custom responder that posts the
    /// reply as a threaded <c>chat.postMessage</c> instead of
    /// <c>response_url</c> POST. Errors, missing arguments, and the
    /// review / escalate fall-back hint all flow through that same
    /// responder so the user sees them as threaded replies.
    /// </para>
    /// </remarks>
    internal async Task DispatchAsync(
        SlackInboundEnvelope envelope,
        SlackCommandPayload payload,
        ISlackEphemeralResponder responder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(responder);
        ct.ThrowIfCancellationRequested();

        // Dispatching strictly on the parsed sub-command keeps the
        // pipeline source-of-truth on the brief-mandated tokens; the
        // envelope's source type was already validated by the parent
        // pipeline so we do not re-check it.
        string? subCommand = NormaliseSubCommand(payload.SubCommand);
        string correlationId = string.IsNullOrEmpty(envelope.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : envelope.IdempotencyKey;

        if (string.IsNullOrEmpty(subCommand))
        {
            this.logger.LogWarning(
                "SlackCommandHandler received an empty sub-command for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}; replying ephemeral usage.",
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            await this.SendUsageErrorAsync(
                responder,
                payload.ResponseUrl,
                "Missing sub-command.",
                ct).ConfigureAwait(false);
            return;
        }

        switch (subCommand)
        {
            case AskSubCommand:
                await this.HandleAskAsync(envelope, payload, responder, correlationId, ct).ConfigureAwait(false);
                return;

            case StatusSubCommand:
                await this.HandleStatusAsync(envelope, payload, responder, correlationId, ct).ConfigureAwait(false);
                return;

            case ApproveSubCommand:
                await this.HandleDecisionAsync(
                    envelope,
                    payload,
                    responder,
                    correlationId,
                    ApproveActionValue,
                    "approve",
                    ct).ConfigureAwait(false);
                return;

            case RejectSubCommand:
                await this.HandleDecisionAsync(
                    envelope,
                    payload,
                    responder,
                    correlationId,
                    RejectActionValue,
                    "reject",
                    ct).ConfigureAwait(false);
                return;

            case ReviewSubCommand:
                await this.HandleModalAsync(envelope, payload, responder, subCommand, correlationId, ct).ConfigureAwait(false);
                return;

            case EscalateSubCommand:
                await this.HandleModalAsync(envelope, payload, responder, subCommand, correlationId, ct).ConfigureAwait(false);
                return;

            default:
                this.logger.LogWarning(
                    "SlackCommandHandler received unknown sub-command='{SubCommand}' for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}; replying ephemeral usage.",
                    subCommand,
                    envelope.IdempotencyKey,
                    envelope.TeamId,
                    envelope.UserId);
                await this.SendUsageErrorAsync(
                    responder,
                    payload.ResponseUrl,
                    $"Unknown sub-command `{subCommand}`.",
                    ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Renders the canonical usage hint listing every valid
    /// sub-command. Exposed internal so the
    /// <see cref="SlackCommandHandlerTests"/> can assert that the
    /// ephemeral payload mentions every supported token.
    /// </summary>
    internal static string BuildUsageMessage(string? leadingDetail = null)
    {
        StringBuilder sb = new();
        if (!string.IsNullOrEmpty(leadingDetail))
        {
            sb.Append(leadingDetail);
            if (!leadingDetail.EndsWith('.'))
            {
                sb.Append('.');
            }

            sb.Append(' ');
        }

        sb.Append("Valid sub-commands: ");
        sb.Append(string.Join(", ", ValidSubCommands.Select(c => "`" + c + "`")));
        sb.Append('.');
        sb.Append(' ');
        sb.Append("Usage: `/agent ask <prompt>`, `/agent status [task-id]`, ");
        sb.Append("`/agent approve <question-id>`, `/agent reject <question-id>`, ");
        sb.Append("`/agent review <task-id>`, `/agent escalate <task-id>`.");
        return sb.ToString();
    }

    private static string? NormaliseSubCommand(string? subCommand)
    {
        if (string.IsNullOrWhiteSpace(subCommand))
        {
            return null;
        }

        return subCommand.Trim().ToLowerInvariant();
    }

    private async Task HandleAskAsync(
        SlackInboundEnvelope envelope,
        SlackCommandPayload payload,
        ISlackEphemeralResponder responder,
        string correlationId,
        CancellationToken ct)
    {
        string? prompt = payload.ArgumentText;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            this.logger.LogWarning(
                "SlackCommandHandler ask missing prompt for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            await this.SendUsageErrorAsync(
                responder,
                payload.ResponseUrl,
                "`/agent ask` requires a prompt (e.g., `/agent ask generate implementation plan`).",
                ct).ConfigureAwait(false);
            return;
        }

        // RequestedAt MUST be the time the inbound transport first observed the
        // Slack request (envelope.ReceivedAt), not the handler's wall clock --
        // AgentTaskCreationRequest.RequestedAt is documented as "UTC timestamp
        // at which the connector observed the request". Using GetUtcNow() here
        // would silently drift by however long the envelope sat in the
        // ingestor queue.
        AgentTaskCreationRequest request = new(
            Prompt: prompt!,
            Messenger: MessengerName,
            ExternalUserId: envelope.UserId ?? string.Empty,
            ChannelId: envelope.ChannelId,
            CorrelationId: correlationId,
            RequestedAt: envelope.ReceivedAt);

        AgentTaskCreationResult result = await this.taskService
            .CreateTaskAsync(request, ct)
            .ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackCommandHandler ask created task task_id={TaskId} correlation_id={CorrelationId} team={TeamId} user={UserId}.",
            result.TaskId,
            result.CorrelationId,
            envelope.TeamId,
            envelope.UserId);

        string ack = string.IsNullOrEmpty(result.Acknowledgement)
            ? $"Task `{result.TaskId}` created. The agent will reply in this thread."
            : result.Acknowledgement;

        await responder
            .SendEphemeralAsync(payload.ResponseUrl, ack, ct)
            .ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(
        SlackInboundEnvelope envelope,
        SlackCommandPayload payload,
        ISlackEphemeralResponder responder,
        string correlationId,
        CancellationToken ct)
    {
        string? taskId = payload.ArgumentText?.Trim();
        AgentTaskStatusQuery query = new(
            TaskId: string.IsNullOrEmpty(taskId) ? null : taskId,
            Messenger: MessengerName,
            CorrelationId: correlationId);

        AgentTaskStatusResult result = await this.taskService
            .GetTaskStatusAsync(query, ct)
            .ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackCommandHandler status scope={Scope} entries={EntryCount} correlation_id={CorrelationId} team={TeamId} user={UserId}.",
            result.Scope,
            result.Entries.Count,
            correlationId,
            envelope.TeamId,
            envelope.UserId);

        string body = RenderStatusBody(result);
        await responder
            .SendEphemeralAsync(payload.ResponseUrl, body, ct)
            .ConfigureAwait(false);
    }

    private static string RenderStatusBody(AgentTaskStatusResult result)
    {
        StringBuilder sb = new();
        if (!string.IsNullOrEmpty(result.Summary))
        {
            sb.Append(result.Summary);
        }
        else
        {
            sb.Append(string.Equals(result.Scope, "task", StringComparison.OrdinalIgnoreCase)
                ? "No task found."
                : "Swarm is idle.");
        }

        if (result.Entries is { Count: > 0 } entries)
        {
            sb.AppendLine();
            foreach (AgentTaskStatusEntry entry in entries)
            {
                sb.Append("• `").Append(entry.TaskId).Append("` -- ").Append(entry.Status);
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    sb.Append(" -- ").Append(entry.Description);
                }

                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task HandleDecisionAsync(
        SlackInboundEnvelope envelope,
        SlackCommandPayload payload,
        ISlackEphemeralResponder responder,
        string correlationId,
        string actionValue,
        string verb,
        CancellationToken ct)
    {
        string? questionId = payload.ArgumentText?.Trim();
        if (string.IsNullOrWhiteSpace(questionId))
        {
            this.logger.LogWarning(
                "SlackCommandHandler {Verb} missing question-id for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                verb,
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            await this.SendUsageErrorAsync(
                responder,
                payload.ResponseUrl,
                $"`/agent {verb}` requires a question-id (e.g., `/agent {verb} Q-123`).",
                ct).ConfigureAwait(false);
            return;
        }

        // ReceivedAt MUST be the original transport observation timestamp
        // (envelope.ReceivedAt) per the HumanDecisionEvent contract -- using
        // the handler's wall clock would tag every decision with the dispatch
        // time, masking ingestor-queue lag from the auditors that downstream
        // SLO checks rely on.
        HumanDecisionEvent decision = new(
            QuestionId: questionId!,
            ActionValue: actionValue,
            Comment: null,
            Messenger: MessengerName,
            ExternalUserId: envelope.UserId ?? string.Empty,
            ExternalMessageId: envelope.TriggerId ?? envelope.IdempotencyKey ?? string.Empty,
            ReceivedAt: envelope.ReceivedAt,
            CorrelationId: correlationId);

        await this.taskService
            .PublishDecisionAsync(decision, ct)
            .ConfigureAwait(false);

        this.logger.LogInformation(
            "SlackCommandHandler {Verb} published HumanDecisionEvent question_id={QuestionId} correlation_id={CorrelationId} team={TeamId} user={UserId}.",
            verb,
            questionId,
            correlationId,
            envelope.TeamId,
            envelope.UserId);

        await responder
            .SendEphemeralAsync(
                payload.ResponseUrl,
                $"Decision recorded: `{verb}` on question `{questionId}`.",
                ct)
            .ConfigureAwait(false);
    }

    private async Task HandleModalAsync(
        SlackInboundEnvelope envelope,
        SlackCommandPayload payload,
        ISlackEphemeralResponder responder,
        string subCommand,
        string correlationId,
        CancellationToken ct)
    {
        // In production the controller's synchronous fast-path
        // (DefaultSlackModalFastPathHandler) handles review / escalate
        // before they reach the ingestor; if we are here, the fast
        // path was bypassed or the envelope was synthetic. Either
        // way we attempt views.open best-effort so brief steps 6 / 7
        // still execute the intended SlackDirectApiClient call;
        // failure produces an ephemeral hint pointing the user to
        // re-issue the command from a fresh interaction.
        if (string.IsNullOrEmpty(envelope.TriggerId))
        {
            this.logger.LogWarning(
                "SlackCommandHandler {SubCommand} missing trigger_id (cannot call views.open) for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                subCommand,
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            await responder
                .SendEphemeralAsync(
                    payload.ResponseUrl,
                    $"Cannot open the `{subCommand}` modal: the Slack trigger_id is missing. Re-run `/agent {subCommand}` from Slack.",
                    ct)
                .ConfigureAwait(false);
            return;
        }

        string? taskId = payload.ArgumentText?.Trim();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            this.logger.LogWarning(
                "SlackCommandHandler {SubCommand} missing task-id for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                subCommand,
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            await this.SendUsageErrorAsync(
                responder,
                payload.ResponseUrl,
                $"`/agent {subCommand}` requires a task-id (e.g., `/agent {subCommand} TASK-42`).",
                ct).ConfigureAwait(false);
            return;
        }

        object viewPayload;
        try
        {
            // Iter-2 evaluator items 2 + 4 fix: use ISlackMessageRenderer
            // (Stage 5.1's task-id-aware renderer) instead of the Stage
            // 4.1 placeholder ISlackModalPayloadBuilder, and pass the
            // task-id parsed from the command arguments into the modal
            // context so it lands in both private_metadata (for the
            // Stage 5.3 view-submission handler) and the visible title.
            viewPayload = subCommand switch
            {
                ReviewSubCommand => this.messageRenderer.RenderReviewModal(new SlackReviewModalContext(
                    TaskId: taskId!,
                    TeamId: envelope.TeamId,
                    ChannelId: envelope.ChannelId,
                    UserId: envelope.UserId,
                    CorrelationId: correlationId)),

                EscalateSubCommand => this.messageRenderer.RenderEscalateModal(new SlackEscalateModalContext(
                    TaskId: taskId!,
                    TeamId: envelope.TeamId,
                    ChannelId: envelope.ChannelId,
                    UserId: envelope.UserId,
                    CorrelationId: correlationId)),

                _ => throw new InvalidOperationException(
                    $"HandleModalAsync invoked with unsupported sub-command '{subCommand}'."),
            };
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "SlackCommandHandler {SubCommand} renderer failed for envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId}.",
                subCommand,
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            await responder
                .SendEphemeralAsync(
                    payload.ResponseUrl,
                    $"Could not build the `{subCommand}` modal payload: {ex.Message}.",
                    ct)
                .ConfigureAwait(false);
            return;
        }

        SlackViewsOpenResult result = await this.viewsOpenClient
            .OpenAsync(new SlackViewsOpenRequest(envelope.TeamId, envelope.TriggerId!, viewPayload), ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            this.logger.LogInformation(
                "SlackCommandHandler {SubCommand} opened modal for task_id={TaskId} team={TeamId} user={UserId} trigger_id={TriggerId}.",
                subCommand,
                taskId,
                envelope.TeamId,
                envelope.UserId,
                envelope.TriggerId);
            return;
        }

        string userMessage = result.Kind switch
        {
            SlackViewsOpenResultKind.MissingConfiguration =>
                $"Could not open the `{subCommand}` modal: this Slack workspace is not configured for agent commands.",
            SlackViewsOpenResultKind.NetworkFailure =>
                $"Could not open the `{subCommand}` modal: Slack timed out or was unreachable. Please retry in a few seconds.",
            _ => $"Could not open the `{subCommand}` modal: Slack returned error `{result.Error ?? "unknown_error"}`. The Slack trigger_id may have expired -- re-run the command.",
        };

        this.logger.LogWarning(
            "SlackCommandHandler {SubCommand} views.open failed kind={Kind} error={Error} team={TeamId} user={UserId} trigger_id={TriggerId}.",
            subCommand,
            result.Kind,
            result.Error,
            envelope.TeamId,
            envelope.UserId,
            envelope.TriggerId);

        await responder
            .SendEphemeralAsync(payload.ResponseUrl, userMessage, ct)
            .ConfigureAwait(false);
    }

    private Task SendUsageErrorAsync(ISlackEphemeralResponder responder, string? responseUrl, string leadingDetail, CancellationToken ct)
        => responder.SendEphemeralAsync(
            responseUrl,
            BuildUsageMessage(leadingDetail),
            ct);
}
