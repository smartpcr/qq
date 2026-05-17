using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Shared implementation backing <see cref="ApproveCommandHandler"/> and
/// <see cref="RejectCommandHandler"/>. Both commands share the same resolution contract per
/// <c>implementation-plan.md</c> §3.2 step 4 — only the canonical action value
/// (<c>"approve"</c> vs <c>"reject"</c>) and the command keyword differ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution path</b> (see <c>implementation-plan.md</c> §3.2 step 4):
/// <list type="number">
/// <item><description><b>Explicit</b>: <c>approve q-123</c> / <c>reject q-456</c> — resolve
/// directly via <see cref="IAgentQuestionStore.GetByIdAsync"/>.</description></item>
/// <item><description><b>Bare single</b>: <c>approve</c> with one open question in the
/// conversation — auto-resolve via the single hit from
/// <see cref="IAgentQuestionStore.GetOpenByConversationAsync"/>.</description></item>
/// <item><description><b>Bare none</b>: <c>approve</c> with zero open questions — reply
/// "no open questions in this conversation".</description></item>
/// <item><description><b>Bare ambiguous</b>: <c>approve</c> with multiple open questions —
/// reply with a disambiguation Adaptive Card listing each question's <c>QuestionId</c>,
/// <c>Title</c>, and <c>CreatedAt</c>. Do NOT resolve any question.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Resolution contract</b> (once a target question is identified by either path):
/// <list type="number">
/// <item><description>Validate that the action value (<c>"approve"</c> / <c>"reject"</c>)
/// is in the question's <see cref="AgentQuestion.AllowedActions"/> list — case-insensitive
/// match on <see cref="HumanAction.Value"/>.</description></item>
/// <item><description>Refuse text resolution when the matched <see cref="HumanAction"/>
/// declares <see cref="HumanAction.RequiresComment"/> — text commands have no inline
/// comment syntax, so the user is told to use the Adaptive Card buttons instead. This
/// prevents text approve/reject from producing a <see cref="HumanDecisionEvent"/> with
/// <see cref="HumanDecisionEvent.Comment"/> = <c>null</c> when the question explicitly
/// requires one.</description></item>
/// <item><description>Atomically transition <see cref="AgentQuestion.Status"/> from
/// <c>"Open"</c> to <c>"Resolved"</c> via
/// <see cref="IAgentQuestionStore.TryUpdateStatusAsync"/>. If the CAS fails (the question
/// was already resolved or expired by another handler / pod), reply
/// "decision already recorded" and do not emit a decision event (first-writer-wins per
/// <c>architecture.md</c> §6.3).</description></item>
/// <item><description>Emit a <see cref="DecisionEvent"/> wrapping a
/// <see cref="HumanDecisionEvent"/> via <see cref="IInboundEventPublisher.PublishAsync"/>
/// so the orchestrator sees the decision exactly as it would for an Adaptive Card
/// invoke.</description></item>
/// <item><description>Reply with a decision-confirmation Adaptive Card built by
/// <see cref="Cards.IAdaptiveCardRenderer.RenderDecisionConfirmationCard(HumanDecisionEvent)"/>.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class ApproveRejectCommandExecutor
{
    private readonly IAgentQuestionStore _questionStore;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly Cards.IAdaptiveCardRenderer _cardRenderer;
    private readonly ILogger _logger;

    public ApproveRejectCommandExecutor(
        IAgentQuestionStore questionStore,
        IInboundEventPublisher inboundEventPublisher,
        Cards.IAdaptiveCardRenderer cardRenderer,
        ILogger logger)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _cardRenderer = cardRenderer ?? throw new ArgumentNullException(nameof(cardRenderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute the approve/reject pipeline for <paramref name="actionValue"/> against the
    /// dispatcher-supplied <paramref name="context"/>.
    /// </summary>
    /// <param name="commandName">The command keyword (<c>"approve"</c> or <c>"reject"</c>) used in user-facing reply strings.</param>
    /// <param name="actionValue">The canonical action value compared against <see cref="HumanAction.Value"/> on the resolved question.</param>
    /// <param name="context">The dispatcher-populated <see cref="CommandContext"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExecuteAsync(string commandName, string actionValue, CommandContext context, CancellationToken ct)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var explicitQuestionId = (context.CommandArguments ?? string.Empty).Trim();
        AgentQuestion? target;

        if (!string.IsNullOrEmpty(explicitQuestionId))
        {
            target = await _questionStore.GetByIdAsync(explicitQuestionId, ct).ConfigureAwait(false);
            if (target is null)
            {
                _logger.LogInformation(
                    "Explicit {Command} '{QuestionId}' rejected — no such question (correlation {CorrelationId}).",
                    commandName,
                    explicitQuestionId,
                    context.CorrelationId);
                // Stage 5.2 iter-4 (eval item 7) — handled command-level rejection is NOT
                // a Success from the audit perspective, even though we return without
                // throwing. Stamp the canonical Rejected outcome onto the context so the
                // activity handler's post-dispatch CommandReceived audit records the
                // truthful outcome (tech-spec.md §4.3).
                context.Outcome = AuditOutcomes.Rejected;
                await ReplyAsync(context, CommandReplyCards.BuildErrorCard(
                    title: "Question not found",
                    detail: $"No question with id `{explicitQuestionId}` exists. Re-issue the command with the correct id."), ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            var conversationId = context.ConversationId ?? string.Empty;
            if (string.IsNullOrEmpty(conversationId))
            {
                _logger.LogWarning(
                    "Bare {Command} could not look up open questions — CommandContext.ConversationId is null (correlation {CorrelationId}).",
                    commandName,
                    context.CorrelationId);
                context.Outcome = AuditOutcomes.Rejected;
                await ReplyAsync(context, CommandReplyCards.BuildErrorCard(
                    title: $"Cannot {commandName}",
                    detail: $"This conversation has no identifier — re-issue the command with an explicit question id (e.g. `{commandName} q-123`)."), ct).ConfigureAwait(false);
                return;
            }

            var open = await _questionStore.GetOpenByConversationAsync(conversationId, ct).ConfigureAwait(false)
                ?? Array.Empty<AgentQuestion>();

            if (open.Count == 0)
            {
                _logger.LogInformation(
                    "Bare {Command} found no open questions in conversation {ConversationId} (correlation {CorrelationId}).",
                    commandName,
                    conversationId,
                    context.CorrelationId);
                context.Outcome = AuditOutcomes.Rejected;
                await ReplyAsync(context, CommandReplyCards.BuildErrorCard(
                    title: $"Nothing to {commandName}",
                    detail: "There are no open questions in this conversation."), ct).ConfigureAwait(false);
                return;
            }

            if (open.Count > 1)
            {
                _logger.LogInformation(
                    "Bare {Command} found {Count} open questions in conversation {ConversationId} — returning disambiguation card (correlation {CorrelationId}).",
                    commandName,
                    open.Count,
                    conversationId,
                    context.CorrelationId);
                context.Outcome = AuditOutcomes.Rejected;
                await ReplyAsync(context, CommandReplyCards.BuildDisambiguationCard(commandName, open), ct).ConfigureAwait(false);
                return;
            }

            target = open[0];
        }

        // Stage 5.2 step 3 — once a target question is identified (explicit ID or
        // bare-single), stamp its AgentId / TaskId onto the CommandContext so the
        // activity handler's post-dispatch CommandReceived audit (per tech-spec.md
        // §4.3) can carry the agent/task association. This MUST happen BEFORE any
        // of the rejection branches below (action-not-allowed, requires-comment,
        // CAS race) so even a failed approve/reject still produces an audit row
        // that points at the affected agent and task — that is exactly the
        // forensic trail the audit table exists for.
        context.AgentId = target.AgentId;
        context.TaskId = target.TaskId;

        // Validate the action value against the question's allowed actions.
        var matchedAction = MatchAction(target, actionValue);
        if (matchedAction is null)
        {
            _logger.LogInformation(
                "{Command} '{QuestionId}' rejected — '{ActionValue}' is not in AllowedActions (correlation {CorrelationId}).",
                commandName,
                target.QuestionId,
                actionValue,
                context.CorrelationId);
            context.Outcome = AuditOutcomes.Rejected;
            var allowed = string.Join(", ", target.AllowedActions.Select(a => a.Value));
            await ReplyAsync(context, CommandReplyCards.BuildErrorCard(
                title: $"Cannot {commandName}",
                detail: $"Question `{target.QuestionId}` does not allow `{actionValue}`. Allowed actions: {allowed}."), ct).ConfigureAwait(false);
            return;
        }

        if (matchedAction.RequiresComment)
        {
            // Text commands lack inline comment syntax — refuse rather than recording an
            // incomplete decision. The user is expected to use the Adaptive Card buttons
            // (which surface the comment Input.Text) per Cards.AdaptiveCardBuilder.
            _logger.LogInformation(
                "{Command} '{QuestionId}' rejected via text — action requires a comment, but text commands have no inline comment syntax (correlation {CorrelationId}).",
                commandName,
                target.QuestionId,
                context.CorrelationId);
            context.Outcome = AuditOutcomes.Rejected;
            await ReplyAsync(context, CommandReplyCards.BuildErrorCard(
                title: "Comment required",
                detail: $"`{commandName}` for `{target.QuestionId}` requires a comment. Please use the card buttons so you can supply one."), ct).ConfigureAwait(false);
            return;
        }

        // Compare-and-set Status from Open → Resolved. First writer wins; concurrent
        // submissions / already-resolved questions produce false.
        var transitioned = await _questionStore
            .TryUpdateStatusAsync(target.QuestionId, AgentQuestionStatuses.Open, AgentQuestionStatuses.Resolved, ct)
            .ConfigureAwait(false);

        if (!transitioned)
        {
            _logger.LogInformation(
                "{Command} '{QuestionId}' raced — question was no longer Open at CAS time (correlation {CorrelationId}).",
                commandName,
                target.QuestionId,
                context.CorrelationId);
            context.Outcome = AuditOutcomes.Rejected;
            await ReplyAsync(context, CommandReplyCards.BuildErrorCard(
                title: "Decision already recorded",
                detail: $"Question `{target.QuestionId}` was resolved by another action before this one was processed."), ct).ConfigureAwait(false);
            return;
        }

        // Build the canonical HumanDecisionEvent and publish via the inbound channel.
        var correlationId = string.IsNullOrEmpty(context.CorrelationId)
            ? target.CorrelationId
            : context.CorrelationId!;

        var decision = new HumanDecisionEvent(
            QuestionId: target.QuestionId,
            ActionValue: matchedAction.Value,
            Comment: null,
            Messenger: "Teams",
            ExternalUserId: context.ResolvedIdentity?.AadObjectId ?? string.Empty,
            ExternalMessageId: context.ActivityId ?? string.Empty,
            ReceivedAt: DateTimeOffset.UtcNow,
            CorrelationId: correlationId);

        var decisionEvent = new DecisionEvent
        {
            EventId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId,
            Messenger = "Teams",
            ExternalUserId = context.ResolvedIdentity?.AadObjectId ?? string.Empty,
            ActivityId = context.ActivityId,
            Source = null,
            Timestamp = decision.ReceivedAt,
            Payload = decision,
        };

        await _inboundEventPublisher.PublishAsync(decisionEvent, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "{Command} '{QuestionId}' resolved (correlation {CorrelationId}, actor {ExternalUserId}).",
            commandName,
            target.QuestionId,
            correlationId,
            decision.ExternalUserId);

        await ReplyAsync(context, CommandReplyCards.BuildDecisionConfirmationCard(_cardRenderer, decision), ct).ConfigureAwait(false);
    }

    private static HumanAction? MatchAction(AgentQuestion question, string actionValue)
    {
        foreach (var action in question.AllowedActions)
        {
            if (string.Equals(action.Value, actionValue, StringComparison.OrdinalIgnoreCase))
            {
                return action;
            }
        }

        return null;
    }

    private static Task ReplyAsync(CommandContext context, Microsoft.Bot.Schema.IMessageActivity reply, CancellationToken ct)
    {
        if (context.TurnContext is ITurnContext turnContext)
        {
            return turnContext.SendActivityAsync(reply, ct);
        }

        return Task.CompletedTask;
    }
}
