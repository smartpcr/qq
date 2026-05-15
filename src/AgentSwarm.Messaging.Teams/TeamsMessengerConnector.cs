using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
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
/// </remarks>
public sealed class TeamsMessengerConnector : IMessengerConnector
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

    /// <summary>
    /// Construct the connector with the dependencies required by
    /// <c>implementation-plan.md</c> §2.3 step 1 and §3.1 step 7, plus a
    /// <see cref="IConversationReferenceRouter"/> for <see cref="SendMessageAsync"/>
    /// routing, an <see cref="IInboundEventReader"/> for <see cref="ReceiveAsync"/>, an
    /// <see cref="IAdaptiveCardRenderer"/> for <see cref="SendQuestionAsync"/> card
    /// rendering, and an <see cref="ILogger{T}"/> for operational logging. Every
    /// parameter is null-guarded so DI mis-registration fails loudly at startup rather
    /// than producing a <see cref="NullReferenceException"/> deep inside an outbound
    /// delivery.
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

        var now = DateTimeOffset.UtcNow;
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
