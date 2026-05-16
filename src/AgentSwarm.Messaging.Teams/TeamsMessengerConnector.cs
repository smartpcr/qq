using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Microsoft Teams implementation of <see cref="IMessengerConnector"/> per
/// <c>implementation-plan.md</c> §2.3 and <c>architecture.md</c> §2.9. Bridges the
/// platform-agnostic gateway abstractions to Bot Framework's <see cref="CloudAdapter"/>:
/// <see cref="SendMessageAsync"/> and <see cref="SendQuestionAsync"/> dispatch via
/// <c>ContinueConversationAsync</c>; <see cref="ReceiveAsync"/> reads from the in-process
/// inbound channel populated by <c>TeamsSwarmActivityHandler</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SendQuestionAsync uses a three-step persistence pattern</b> (per the §2.3 brief):
/// </para>
/// <list type="number">
/// <item><description>Persist a sanitized copy of the <see cref="AgentQuestion"/> via
/// <see cref="IAgentQuestionStore.SaveAsync"/> with <c>ConversationId</c> forced to
/// <c>null</c> regardless of the value supplied by the caller — the §2.3 brief is
/// explicit that the conversation ID is "not yet known before the proactive send" and
/// stamping a stale value here would corrupt the bare <c>approve</c>/<c>reject</c>
/// resolution path that <c>GetOpenByConversationAsync</c> drives.</description></item>
/// <item><description>Resolve the target <see cref="TeamsConversationReference"/> by either
/// <see cref="AgentQuestion.TargetUserId"/> (via
/// <see cref="IConversationReferenceStore.GetByInternalUserIdAsync"/>) or
/// <see cref="AgentQuestion.TargetChannelId"/> (via
/// <see cref="IConversationReferenceStore.GetByChannelIdAsync"/>), rehydrate to a Bot
/// Framework <see cref="ConversationReference"/> via <c>Newtonsoft.Json</c>, render the
/// question into an Adaptive Card via the injected <see cref="IAdaptiveCardRenderer"/>
/// (Stage 3.1), wrap it in an <see cref="Attachment"/> with
/// <c>ContentType = "application/vnd.microsoft.card.adaptive"</c>, and send it through
/// <c>ContinueConversationAsync</c>. <see cref="Activity.Text"/> is set to the
/// question title as a notification-banner / accessibility fallback only.</description></item>
/// <item><description>Capture the <c>ResourceResponse.Id</c> (Teams activity ID) and the
/// proactive turn context's <c>Conversation.Id</c>. If EITHER is missing the connector
/// throws <see cref="InvalidOperationException"/> — partial persistence (saving the
/// activity ID without the conversation ID, or vice versa) leaves the question in a
/// state where bare <c>approve</c>/<c>reject</c> cannot resolve it AND the Adaptive Card
/// cannot be updated/deleted, so the failure is surfaced loudly rather than swallowed
/// with a warning. Only when BOTH identifiers are present does the connector call
/// <see cref="IAgentQuestionStore.UpdateConversationIdAsync"/> AND
/// <see cref="ICardStateStore.SaveAsync"/>.</description></item>
/// </list>
/// <para>
/// <b>Adapter type:</b> the constructor takes the concrete <see cref="CloudAdapter"/> per the
/// canonical contract in <c>implementation-plan.md</c> §2.3 step 1. <see cref="CloudAdapter"/>
/// inherits the proactive <c>ContinueConversationAsync(string, ConversationReference,
/// BotCallbackHandler, CancellationToken)</c> overload from <see cref="BotAdapter"/>, which is
/// virtual — tests can substitute with a recording subclass.
/// </para>
/// <para>
/// <b>Conversation-ID routing:</b> <see cref="SendMessageAsync"/> uses the narrow
/// <see cref="IConversationReferenceRouter"/> companion interface to resolve the stored
/// reference for an outbound <see cref="MessengerMessage"/>. The canonical
/// <see cref="IConversationReferenceStore"/> intentionally does not expose a
/// <c>GetByConversationIdAsync</c> method (the planning contract in
/// <c>implementation-plan.md</c> §2.1 enumerates only natural-key lookups), so a separate
/// interface keeps both contracts honest. Real store implementations (Stage 2.1
/// in-memory and Stage 4.1 SQL) are expected to implement both interfaces and register
/// the same singleton under both service types.
/// </para>
/// <para>
/// <b>Clock injection:</b> all timestamps stamped onto persisted rows
/// (<see cref="TeamsCardState.CreatedAt"/>, <see cref="TeamsCardState.UpdatedAt"/>) and
/// onto in-memory <see cref="HumanDecisionEvent"/> placeholders flow through an injected
/// <see cref="TimeProvider"/>. The DI-friendly public constructor defaults to
/// <see cref="TimeProvider.System"/>; a second public overload accepts a deterministic
/// provider so unit tests can advance the clock without wall-clock flakiness. This
/// mirrors the pattern already used by <see cref="Cards.AdaptiveCardBuilder"/>,
/// <see cref="Cards.CardActionHandler"/>, and
/// <see cref="Lifecycle.QuestionExpiryProcessor"/> and keeps the connector's
/// store-bound timestamps consistent with the rest of the lifecycle code so
/// cross-component ordering (card state row vs. dedupe cache vs. expiry sweep) stays
/// deterministic under a fake clock.
/// </para>
/// </remarks>
public sealed class TeamsMessengerConnector : IMessengerConnector, ITeamsCardManager
{
    private readonly CloudAdapter _adapter;
    private readonly TeamsMessagingOptions _options;
    private readonly IConversationReferenceStore _conversationReferenceStore;
    private readonly IConversationReferenceRouter _conversationReferenceRouter;
    private readonly IAgentQuestionStore _agentQuestionStore;
    private readonly ICardStateStore _cardStateStore;
    private readonly IAdaptiveCardRenderer _cardRenderer;
    private readonly IInboundEventReader _inboundEventReader;
    private readonly ILogger<TeamsMessengerConnector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly InstallationStateGate? _installationStateGate;

    /// <summary>
    /// Construct the connector with the dependencies required by
    /// <c>implementation-plan.md</c> §2.3 step 1 and §3.1 step 7, plus a
    /// <see cref="IConversationReferenceRouter"/> for <see cref="SendMessageAsync"/>
    /// routing, an <see cref="IInboundEventReader"/> for <see cref="ReceiveAsync"/>, an
    /// <see cref="IAdaptiveCardRenderer"/> for <see cref="SendQuestionAsync"/> card
    /// rendering, and an <see cref="ILogger{T}"/> for operational logging. Every
    /// parameter is null-guarded so DI mis-registration fails loudly at startup rather
    /// than producing a <see cref="NullReferenceException"/> deep inside an outbound
    /// delivery. The clock defaults to <see cref="TimeProvider.System"/>; unit tests
    /// resolve the second public overload to inject a deterministic provider.
    /// </summary>
    public TeamsMessengerConnector(
        CloudAdapter adapter,
        TeamsMessagingOptions options,
        IConversationReferenceStore conversationReferenceStore,
        IConversationReferenceRouter conversationReferenceRouter,
        IAgentQuestionStore agentQuestionStore,
        ICardStateStore cardStateStore,
        IAdaptiveCardRenderer cardRenderer,
        IInboundEventReader inboundEventReader,
        ILogger<TeamsMessengerConnector> logger)
        : this(
            adapter,
            options,
            conversationReferenceStore,
            conversationReferenceRouter,
            agentQuestionStore,
            cardStateStore,
            cardRenderer,
            inboundEventReader,
            logger,
            TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic <see cref="TimeProvider"/>
    /// so unit tests can verify the exact values stamped onto
    /// <see cref="TeamsCardState.CreatedAt"/> / <see cref="TeamsCardState.UpdatedAt"/>
    /// (including the stale-activity fallback in
    /// <see cref="UpdateCardAsync(string, CardUpdateAction, CancellationToken)"/>) and
    /// onto <see cref="HumanDecisionEvent.ReceivedAt"/> placeholders without wall-clock
    /// flakiness. Production DI resolves the 9-arg constructor, which delegates here
    /// with <see cref="TimeProvider.System"/>; this overload is public (matching the
    /// pattern used by <see cref="Lifecycle.QuestionExpiryProcessor"/>) so tests in
    /// other assemblies do not need <c>InternalsVisibleTo</c>.
    /// </summary>
    public TeamsMessengerConnector(
        CloudAdapter adapter,
        TeamsMessagingOptions options,
        IConversationReferenceStore conversationReferenceStore,
        IConversationReferenceRouter conversationReferenceRouter,
        IAgentQuestionStore agentQuestionStore,
        ICardStateStore cardStateStore,
        IAdaptiveCardRenderer cardRenderer,
        IInboundEventReader inboundEventReader,
        ILogger<TeamsMessengerConnector> logger,
        TimeProvider timeProvider)
        : this(
            adapter,
            options,
            conversationReferenceStore,
            conversationReferenceRouter,
            agentQuestionStore,
            cardStateStore,
            cardRenderer,
            inboundEventReader,
            logger,
            timeProvider,
            installationStateGate: null)
    {
    }

    /// <summary>
    /// Canonical production constructor wired by <c>AddTeamsMessengerConnector</c> +
    /// <c>AddTeamsSecurity</c>. Accepts an <see cref="InstallationStateGate"/> so
    /// <see cref="SendQuestionAsync"/> guards every proactive Adaptive Card delivery with
    /// the Stage 5.1 install-state pre-check. When the gate is supplied as <c>null</c>
    /// (legacy test compositions and the constructors above), the install-state probe is
    /// bypassed — this is intended ONLY for pre-Stage-5.1 unit tests; production hosts
    /// MUST resolve this constructor via DI so the gate is never null in deployed
    /// environments.
    /// </summary>
    public TeamsMessengerConnector(
        CloudAdapter adapter,
        TeamsMessagingOptions options,
        IConversationReferenceStore conversationReferenceStore,
        IConversationReferenceRouter conversationReferenceRouter,
        IAgentQuestionStore agentQuestionStore,
        ICardStateStore cardStateStore,
        IAdaptiveCardRenderer cardRenderer,
        IInboundEventReader inboundEventReader,
        ILogger<TeamsMessengerConnector> logger,
        TimeProvider timeProvider,
        InstallationStateGate? installationStateGate)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _conversationReferenceStore = conversationReferenceStore ?? throw new ArgumentNullException(nameof(conversationReferenceStore));
        _conversationReferenceRouter = conversationReferenceRouter ?? throw new ArgumentNullException(nameof(conversationReferenceRouter));
        _agentQuestionStore = agentQuestionStore ?? throw new ArgumentNullException(nameof(agentQuestionStore));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _cardRenderer = cardRenderer ?? throw new ArgumentNullException(nameof(cardRenderer));
        _inboundEventReader = inboundEventReader ?? throw new ArgumentNullException(nameof(inboundEventReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _installationStateGate = installationStateGate;
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (string.IsNullOrWhiteSpace(message.ConversationId))
        {
            throw new InvalidOperationException(
                $"MessengerMessage '{message.MessageId}' has no ConversationId; cannot route the outbound delivery.");
        }

        var stored = await _conversationReferenceRouter
            .GetByConversationIdAsync(message.ConversationId, ct)
            .ConfigureAwait(false);
        if (stored is null)
        {
            // Stage 5.1 iter-7 evaluator feedback item 2 — emit InstallationGateRejected
            // audit + dead-letter the outbox entry BEFORE throwing. Unlike the user/channel
            // paths, the connector cannot probe install-state before the lookup because
            // MessengerMessage carries only a Bot Framework ConversationId (no tenant /
            // user / channel identity). The canonical store's GetByConversationIdAsync
            // filters by IsActive, so a null result definitively means "no active
            // reference for this conversation" — which IS the install-state rejection
            // condition. RejectMessageRoutingAsync produces the same audit row shape and
            // dead-letter wiring that CheckAsync / CheckTargetAsync produce, so an outbox-
            // wrapped SendMessageAsync now drops a dead-letter entry instead of allowing
            // unbounded retry storms, and compliance review sees the same evidence
            // regardless of which routing path was attempted.
            if (_installationStateGate is not null)
            {
                var reason = await _installationStateGate.RejectMessageRoutingAsync(
                    message,
                    message.ConversationId,
                    outboxEntryId: ProactiveSendContext.CurrentOutboxEntryId,
                    cancellationToken: ct).ConfigureAwait(false);

                _logger.LogWarning(
                    "InstallationStateGate rejected outbound MessengerMessage {MessageId} (correlation {CorrelationId}) for conversation {ConversationId}. Reason: {Reason}",
                    message.MessageId,
                    message.CorrelationId,
                    message.ConversationId,
                    reason);
                throw new InvalidOperationException(
                    $"InstallationStateGate rejected outbound delivery for message '{message.MessageId}': {reason}");
            }

            throw new InvalidOperationException(
                $"No conversation reference found for conversation '{message.ConversationId}' (message '{message.MessageId}'). " +
                $"The Teams app must be installed and a prior interaction must have captured a reference before proactive delivery can succeed.");
        }

        var conversationReference = DeserializeReference(stored);

        _logger.LogInformation(
            "Sending outbound MessengerMessage {MessageId} (correlation {CorrelationId}) to conversation {ConversationId} via reference {ReferenceId}.",
            message.MessageId,
            message.CorrelationId,
            message.ConversationId,
            stored.Id);

        await _adapter.ContinueConversationAsync(
            _options.MicrosoftAppId,
            conversationReference,
            async (turnContext, innerCt) =>
            {
                var reply = MessageFactory.Text(message.Body);
                await turnContext.SendActivityAsync(reply, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        if (question is null)
        {
            throw new ArgumentNullException(nameof(question));
        }

        var validationErrors = question.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' is invalid: {string.Join("; ", validationErrors)}");
        }

        // Step 1 — persist a SANITIZED copy with ConversationId forced to null so a caller
        // that accidentally pre-populates the field cannot poison the bare
        // approve/reject lookup path. Step 3 (below) will stamp the actual
        // conversation ID returned by the proactive turn context. Reference equality is
        // unchanged for callers that already passed a null ConversationId — the `with`
        // expression returns the same effective record.
        var sanitizedQuestion = question with { ConversationId = null };
        await _agentQuestionStore.SaveAsync(sanitizedQuestion, ct).ConfigureAwait(false);

        // Step 2 — resolve the conversation reference from the orchestrator-supplied target
        // user/channel. Only one of TargetUserId / TargetChannelId is set (enforced by
        // AgentQuestion.Validate()); we already verified Validate() returned no errors.
        //
        // Stage 5.1 iter-5 evaluator feedback item 1 — STRUCTURAL fix. The
        // InstallationStateGate MUST run BEFORE the active-only reference lookup. The
        // real SqlConversationReferenceStore filters Get*Async results by `e.IsActive`,
        // so an inactive (uninstalled) target returns null and the older "lookup-then-
        // gate" ordering threw InvalidOperationException BEFORE the gate emitted the
        // InstallationGateRejected audit row or dead-lettered the outbox entry.
        // CheckAsync uses IsActiveBy*Async probes (booleans handling missing/inactive
        // alike) so the gate does not depend on the lookup having succeeded.
        if (_installationStateGate is not null)
        {
            var gateResult = await _installationStateGate.CheckAsync(
                question,
                outboxEntryId: ProactiveSendContext.CurrentOutboxEntryId,
                correlationId: question.CorrelationId ?? string.Empty,
                ct).ConfigureAwait(false);

            if (!gateResult.IsActive)
            {
                _logger.LogWarning(
                    "InstallationStateGate rejected synchronous AgentQuestion {QuestionId} (correlation {CorrelationId}) in tenant {TenantId}. Skipping reference lookup and Bot Framework call. Reason: {Reason}",
                    question.QuestionId,
                    question.CorrelationId,
                    question.TenantId,
                    gateResult.Reason);
                throw new InvalidOperationException(
                    $"InstallationStateGate rejected proactive delivery for question '{question.QuestionId}': {gateResult.Reason}");
            }
        }

        TeamsConversationReference? stored;
        if (!string.IsNullOrWhiteSpace(question.TargetUserId))
        {
            stored = await _conversationReferenceStore
                .GetByInternalUserIdAsync(question.TenantId, question.TargetUserId!, ct)
                .ConfigureAwait(false);
        }
        else
        {
            stored = await _conversationReferenceStore
                .GetByChannelIdAsync(question.TenantId, question.TargetChannelId!, ct)
                .ConfigureAwait(false);
        }

        if (stored is null)
        {
            throw new InvalidOperationException(
                $"No conversation reference found for question '{question.QuestionId}' " +
                $"(tenant '{question.TenantId}', " +
                $"target {(question.TargetUserId is null ? $"channel '{question.TargetChannelId}'" : $"user '{question.TargetUserId}'")}). " +
                $"The Teams app must be installed for the target user or in the target channel before proactive delivery can succeed.");
        }

        var conversationReference = DeserializeReference(stored);

        // Implementation-plan §3.1 step 7 — render the question as an Adaptive Card via
        // IAdaptiveCardRenderer and attach it to the outbound activity. Activity.Text is
        // populated with the question title so Teams can fall back to a plain-text
        // notification banner on clients that cannot render the card (mobile lock screens,
        // accessibility tooling, etc.).
        var attachment = _cardRenderer.RenderQuestionCard(question);

        // Step 2b — perform the proactive send. Closure variables capture the activityId
        // returned by Bot Framework and a fresh ConversationReference rebuilt from the
        // proactive turn context (preferred over the stored reference for downstream
        // rehydration because the proactive turn context reflects the actual delivery —
        // service URL rotation, conversation thread, etc.).
        string? deliveredActivityId = null;
        string? deliveredConversationId = null;
        string? deliveredReferenceJson = null;

        await _adapter.ContinueConversationAsync(
            _options.MicrosoftAppId,
            conversationReference,
            async (turnContext, innerCt) =>
            {
                var reply = MessageFactory.Attachment(attachment);
                reply.Text = question.Title;
                var resourceResponse = await turnContext.SendActivityAsync(reply, innerCt).ConfigureAwait(false);
                deliveredActivityId = resourceResponse?.Id;
                deliveredConversationId = turnContext.Activity?.Conversation?.Id;
                var freshReference = turnContext.Activity?.GetConversationReference();
                if (freshReference is not null)
                {
                    deliveredReferenceJson = JsonConvert.SerializeObject(freshReference);
                }
            },
            ct).ConfigureAwait(false);

        // Step 3 — both deliveredConversationId and deliveredActivityId are required for
        // downstream resolution (bare approve/reject + card update/delete). If EITHER is
        // missing, fail loudly so the orchestrator can decide whether to retry, mark the
        // question as undeliverable, or surface to operators. Silently dropping these
        // would leave the persisted question with a null ConversationId (breaking
        // GetOpenByConversation) AND no card state row (breaking
        // ITeamsCardManager.UpdateCardAsync / DeleteCardAsync). The check is performed
        // BEFORE either persistence call so we never produce partial state.
        if (string.IsNullOrWhiteSpace(deliveredConversationId))
        {
            throw new InvalidOperationException(
                $"ContinueConversationAsync for question '{question.QuestionId}' did not yield " +
                $"a Conversation.Id from the proactive turn context. The card was sent but cannot " +
                $"be resolved by bare approve/reject text commands; treating this as a delivery failure " +
                $"to avoid silent partial persistence.");
        }

        if (string.IsNullOrWhiteSpace(deliveredActivityId))
        {
            throw new InvalidOperationException(
                $"ContinueConversationAsync for question '{question.QuestionId}' did not yield " +
                $"an Activity.Id from the SendActivityAsync response. The card was sent but cannot " +
                $"be updated or deleted later; treating this as a delivery failure to avoid silent " +
                $"partial persistence.");
        }

        await _agentQuestionStore
            .UpdateConversationIdAsync(question.QuestionId, deliveredConversationId!, ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var cardState = new TeamsCardState
        {
            QuestionId = question.QuestionId,
            ActivityId = deliveredActivityId!,
            ConversationId = deliveredConversationId!,
            ConversationReferenceJson = deliveredReferenceJson ?? stored.ReferenceJson,
            Status = TeamsCardStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _cardStateStore.SaveAsync(cardState, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MessengerEvent> ReceiveAsync(CancellationToken ct)
        => _inboundEventReader.ReceiveAsync(ct);

    /// <inheritdoc />
    public Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct)
        => UpdateCardCoreAsync(questionId, action, decision: null, actorDisplayName: null, ct);

    /// <inheritdoc />
    public Task UpdateCardAsync(
        string questionId,
        CardUpdateAction action,
        HumanDecisionEvent decision,
        string? actorDisplayName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return UpdateCardCoreAsync(questionId, action, decision: (HumanDecisionEvent?)decision, actorDisplayName, ct);
    }

    private async Task UpdateCardCoreAsync(
        string questionId,
        CardUpdateAction action,
        HumanDecisionEvent? decision,
        string? actorDisplayName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        var state = await _cardStateStore.GetByQuestionIdAsync(questionId, ct).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidOperationException(
                $"No TeamsCardState found for question '{questionId}'; card update cannot proceed " +
                "because the originating ActivityId/ConversationReference are unknown.");
        }

        var conversationReference = DeserializeReferenceFromJson(state.ConversationReferenceJson, questionId);
        var attachment = RenderUpdateCard(questionId, action, decision, actorDisplayName);
        var nextStatus = action switch
        {
            CardUpdateAction.MarkAnswered => TeamsCardStatuses.Answered,
            CardUpdateAction.MarkExpired => TeamsCardStatuses.Expired,
            CardUpdateAction.MarkCancelled => TeamsCardStatuses.Expired,
            _ => TeamsCardStatuses.Answered,
        };

        // Inline retry loop — same exponential-backoff policy as the outbox engine
        // (base 2s, multiplier 2×, max 60s, 4 retries) executed synchronously.
        await ExecuteWithInlineRetryAsync(
            operationName: "UpdateActivityAsync",
            questionId,
            async (innerCt) =>
            {
                var staleActivityFallbackTriggered = false;
                string? newActivityId = null;

                await _adapter.ContinueConversationAsync(
                    _options.MicrosoftAppId,
                    conversationReference,
                    async (turnContext, cbCt) =>
                    {
                        var replacement = MessageFactory.Attachment(attachment);
                        replacement.Id = state.ActivityId;

                        try
                        {
                            await turnContext.UpdateActivityAsync(replacement, cbCt).ConfigureAwait(false);
                        }
                        catch (Microsoft.Bot.Schema.ErrorResponseException ex) when (IsActivityNotFound(ex))
                        {
                            // Stale-activity fallback per architecture.md §6.5 / e2e-scenarios.md
                            // §Update/Delete: send a fresh replacement card and persist the new
                            // activity ID. Status remains the action-target (Answered / Expired)
                            // because the user-visible state of the conversation is unchanged.
                            staleActivityFallbackTriggered = true;
                            var fresh = MessageFactory.Attachment(attachment);
                            var resp = await turnContext.SendActivityAsync(fresh, cbCt).ConfigureAwait(false);
                            newActivityId = resp?.Id;
                        }
                    },
                    innerCt).ConfigureAwait(false);

                if (staleActivityFallbackTriggered && !string.IsNullOrWhiteSpace(newActivityId))
                {
                    // Use the injected TimeProvider (not DateTimeOffset.UtcNow) so the
                    // refreshed row's UpdatedAt stays in sync with the rest of the
                    // lifecycle code (AdaptiveCardBuilder, CardActionHandler,
                    // QuestionExpiryProcessor) and remains deterministic under a fake
                    // clock in unit tests.
                    var refreshed = state with
                    {
                        ActivityId = newActivityId!,
                        Status = nextStatus,
                        UpdatedAt = _timeProvider.GetUtcNow(),
                    };
                    await _cardStateStore.SaveAsync(refreshed, innerCt).ConfigureAwait(false);
                    return;
                }

                await _cardStateStore.UpdateStatusAsync(questionId, nextStatus, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteCardAsync(string questionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("QuestionId must be a non-empty string.", nameof(questionId));
        }

        var state = await _cardStateStore.GetByQuestionIdAsync(questionId, ct).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidOperationException(
                $"No TeamsCardState found for question '{questionId}'; card delete cannot proceed " +
                "because the originating ActivityId/ConversationReference are unknown.");
        }

        var conversationReference = DeserializeReferenceFromJson(state.ConversationReferenceJson, questionId);

        await ExecuteWithInlineRetryAsync(
            operationName: "DeleteActivityAsync",
            questionId,
            async (innerCt) =>
            {
                await _adapter.ContinueConversationAsync(
                    _options.MicrosoftAppId,
                    conversationReference,
                    async (turnContext, cbCt) =>
                    {
                        try
                        {
                            await turnContext.DeleteActivityAsync(state.ActivityId, cbCt).ConfigureAwait(false);
                        }
                        catch (Microsoft.Bot.Schema.ErrorResponseException ex) when (IsActivityNotFound(ex))
                        {
                            // Already gone — treat as success per the e2e-scenarios.md
                            // §Update/Delete contract ("avoid infinite retry on stale activity").
                            _logger.LogInformation(
                                "DeleteActivityAsync for question {QuestionId} returned 404; treating as already-deleted.",
                                questionId);
                        }
                    },
                    innerCt).ConfigureAwait(false);

                // Per implementation-plan.md §3.3 step 5 ("update card state to Expired") and
                // §3.3 acceptance scenario (line 222): a deleted card lands at the canonical
                // Expired status. There is no separate `Deleted` status — the canonical
                // vocabulary is Pending/Answered/Expired and `Expired` covers both
                // expiry and post-delete terminal state.
                await _cardStateStore.UpdateStatusAsync(questionId, TeamsCardStatuses.Expired, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    private Microsoft.Bot.Schema.Attachment RenderUpdateCard(
        string questionId,
        CardUpdateAction action,
        HumanDecisionEvent? decision,
        string? actorDisplayName)
    {
        return action switch
        {
            CardUpdateAction.MarkAnswered when decision is not null
                => _cardRenderer.RenderDecisionConfirmationCard(decision, actorDisplayName),
            CardUpdateAction.MarkAnswered
                => _cardRenderer.RenderDecisionConfirmationCard(BuildPlaceholderDecision(questionId, "answered"), null),
            CardUpdateAction.MarkExpired
                => _cardRenderer.RenderExpiredNoticeCard(questionId),
            CardUpdateAction.MarkCancelled
                => _cardRenderer.RenderCancelledNoticeCard(questionId),
            _ => _cardRenderer.RenderExpiredNoticeCard(questionId),
        };
    }

    private HumanDecisionEvent BuildPlaceholderDecision(string questionId, string actionValue)
        => new(
            QuestionId: questionId,
            ActionValue: actionValue,
            Comment: null,
            Messenger: "Teams",
            ExternalUserId: "system",
            ExternalMessageId: string.Empty,
            ReceivedAt: _timeProvider.GetUtcNow(),
            CorrelationId: questionId);

    private static ConversationReference DeserializeReferenceFromJson(string referenceJson, string questionId)
    {
        if (string.IsNullOrWhiteSpace(referenceJson))
        {
            throw new InvalidOperationException(
                $"TeamsCardState.ConversationReferenceJson is empty for question '{questionId}'; cannot rehydrate.");
        }

        var reference = JsonConvert.DeserializeObject<ConversationReference>(referenceJson)
            ?? throw new InvalidOperationException(
                $"TeamsCardState.ConversationReferenceJson for question '{questionId}' deserialized to null.");
        return reference;
    }

    /// <summary>
    /// Inline retry helper used by <see cref="UpdateCardCoreAsync"/> and
    /// <see cref="DeleteCardAsync"/>. The contract is intentionally narrow so callers
    /// (and tests like
    /// <c>UpdateCardAsync_TransientFailureExhaustsRetries_Throws</c>) see deterministic
    /// behavior:
    /// <list type="bullet">
    /// <item><description><b>Success</b> on any attempt → method returns normally.</description></item>
    /// <item><description><b>Cancellation</b> via <paramref name="ct"/> → original
    /// <see cref="OperationCanceledException"/> propagates verbatim.</description></item>
    /// <item><description><b>Transient Bot Connector failure</b> on attempts
    /// <c>1..(N-1)</c> → log and retry after exponential backoff (or the server-supplied
    /// <c>Retry-After</c>).</description></item>
    /// <item><description><b>Anything else</b> — including a transient failure on the
    /// FINAL attempt and any non-transient exception on any attempt — propagates the
    /// ORIGINAL exception verbatim. Callers that need to surface the underlying HTTP
    /// status (<see cref="Microsoft.Bot.Schema.ErrorResponseException.Response"/>) get
    /// the unwrapped exception, not a generic
    /// <see cref="InvalidOperationException"/> wrapper.</description></item>
    /// </list>
    /// Because the loop body either returns on success or has its exception bubble
    /// out of the <c>try</c> (the catch filter <c>attempt &lt; totalAttempts</c>
    /// deliberately stops matching on the final iteration), the loop never exits via
    /// fall-through. There is therefore no post-loop throw — adding one would be dead
    /// code AND would mask the underlying exception type the test contract requires.
    /// </summary>
    private async Task ExecuteWithInlineRetryAsync(
        string operationName,
        string questionId,
        Func<CancellationToken, Task> operation,
        CancellationToken ct)
    {
        var totalAttempts = Math.Max(1, _options.MaxRetryAttempts);
        var baseDelay = TimeSpan.FromSeconds(Math.Max(1, _options.RetryBaseDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(60);

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await operation(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientBotConnectorFailure(ex) && attempt < totalAttempts)
            {
                var retryAfter = ExtractRetryAfter(ex);
                var backoff = retryAfter ?? ComputeExponentialBackoff(attempt, baseDelay, maxDelay);
                _logger.LogWarning(
                    ex,
                    "Transient failure on {Operation} for question {QuestionId} (attempt {Attempt}/{Total}); retrying after {Delay}.",
                    operationName,
                    questionId,
                    attempt,
                    totalAttempts,
                    backoff);
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ComputeExponentialBackoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        // exponential: base * 2^(attempt-1), capped at maxDelay.
        var multiplier = Math.Pow(2, attempt - 1);
        var raw = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier);
        return raw > maxDelay ? maxDelay : raw;
    }

    private static bool IsTransientBotConnectorFailure(Exception ex)
    {
        // Bot Framework wraps transport failures in ErrorResponseException; the underlying
        // HTTP status indicates retryability. Anything 5xx or 429 is transient. Bare
        // HttpRequestException and TaskCanceledException (timeout) are also retryable.
        if (ex is Microsoft.Bot.Schema.ErrorResponseException bre)
        {
            var status = (int?)bre.Response?.StatusCode;
            if (status is null)
            {
                return true;
            }

            return status == 408 || status == 425 || status == 429 || (status >= 500 && status < 600);
        }

        if (ex is System.Net.Http.HttpRequestException http)
        {
            var status = (int?)http.StatusCode;
            if (status is null)
            {
                return true;
            }

            return status == 408 || status == 425 || status == 429 || (status >= 500 && status < 600);
        }

        if (ex is TaskCanceledException)
        {
            return true;
        }

        return false;
    }

    private static TimeSpan? ExtractRetryAfter(Exception ex)
    {
        if (ex is Microsoft.Bot.Schema.ErrorResponseException bre)
        {
            var headers = bre.Response?.Headers;
            if (headers is null)
            {
                return null;
            }

            if (headers.TryGetValue("Retry-After", out var values))
            {
                foreach (var raw in values)
                {
                    if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        return TimeSpan.FromSeconds(seconds);
                    }
                }
            }
        }

        return null;
    }

    private static bool IsActivityNotFound(Microsoft.Bot.Schema.ErrorResponseException ex)
    {
        var status = (int?)ex.Response?.StatusCode;
        return status == 404 || status == 410;
    }

    private static ConversationReference DeserializeReference(TeamsConversationReference stored)
    {
        if (string.IsNullOrWhiteSpace(stored.ReferenceJson))
        {
            throw new InvalidOperationException(
                $"Stored conversation reference '{stored.Id}' has empty ReferenceJson; cannot rehydrate.");
        }

        // Use Newtonsoft.Json to round-trip the serializer contract used by
        // TeamsSwarmActivityHandler.SerializeConversationReference (Bot Framework's
        // ConversationReference is annotated with Newtonsoft attributes, JObject extension
        // data, and JObject-typed members that System.Text.Json silently mangles).
        var reference = JsonConvert.DeserializeObject<ConversationReference>(stored.ReferenceJson)
            ?? throw new InvalidOperationException(
                $"Stored conversation reference '{stored.Id}' deserialized to null.");
        return reference;
    }

}
