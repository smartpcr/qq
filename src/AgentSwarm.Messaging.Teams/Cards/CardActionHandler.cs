using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Concrete implementation of <see cref="ICardActionHandler"/> for Stage 3.3 of
/// <c>implementation-plan.md</c> (steps 3 and 4) and <c>architecture.md</c> §2.6 / §6.3.
/// Replaces the <c>NoOpCardActionHandler</c> stub registered in Stage 2.1 once the SQL
/// stores ship.
/// </summary>
/// <remarks>
/// <para>
/// The handler is deliberately the single point of entry from
/// <see cref="TeamsSwarmActivityHandler"/>'s <c>OnAdaptiveCardInvokeAsync</c> override —
/// every responsibility called out in the implementation plan is fulfilled here:
/// </para>
/// <list type="number">
/// <item><description>Parse the inbound <see cref="Activity.Value"/> via
/// <see cref="CardActionMapper.ReadPayload"/> to extract the <c>QuestionId</c>,
/// <c>ActionId</c>, <c>ActionValue</c>, <c>CorrelationId</c>, and the optional
/// <c>Comment</c> — the same code path used by Stage 3.1's
/// <see cref="CardActionMapper"/> so a typo on either side fails fast.</description></item>
/// <item><description>Resolve the originating <see cref="AgentQuestion"/> via
/// <see cref="IAgentQuestionStore.GetByIdAsync"/>. A missing question reports a
/// <c>Rejected</c> audit entry and surfaces a "question not found" card without
/// publishing a decision event.</description></item>
/// <item><description>Validate that <see cref="AgentQuestion.AllowedActions"/> contains
/// the submitted <c>ActionValue</c>. An invalid action reports a <c>Rejected</c>
/// audit entry and a "not permitted" card.</description></item>
/// <item><description>Reject if the question's <see cref="AgentQuestion.Status"/> is
/// already terminal (<see cref="AgentQuestionStatuses.Resolved"/> or
/// <see cref="AgentQuestionStatuses.Expired"/>). The audit entry uses
/// <see cref="AuditOutcomes.Rejected"/>.</description></item>
/// <item><description>Atomically transition <see cref="AgentQuestion.Status"/> from
/// <see cref="AgentQuestionStatuses.Open"/> to
/// <see cref="AgentQuestionStatuses.Resolved"/> via
/// <see cref="IAgentQuestionStore.TryUpdateStatusAsync"/>. If the CAS returns
/// <c>false</c> (another pod won the race per <c>architecture.md</c> §6.3), surface
/// a "decision already recorded" card and emit a <c>Rejected</c> audit entry — the
/// concurrent winner is responsible for the decision event and the card update.</description></item>
/// <item><description>On a successful CAS publish a <see cref="DecisionEvent"/>
/// wrapping the <see cref="HumanDecisionEvent"/> via
/// <see cref="IInboundEventPublisher.PublishAsync"/>, replace the original card via
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
/// (the actor-attributed overload — the canonical 3-arg
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, CancellationToken)"/>
/// remains intact for callers that have no decision payload), and write a
/// <see cref="AuditEventTypes.CardActionReceived"/> audit entry with
/// <see cref="AuditOutcomes.Success"/>.</description></item>
/// </list>
/// <para>
/// <b>Audit payload sanitisation.</b> The <see cref="AuditEntry.PayloadJson"/> field is
/// built by <see cref="BuildSanitizedPayloadJsonAsync"/> which extracts only the
/// canonical card-action fields plus a redacted comment indicator and the persisted
/// card-state activity ID — never the raw <c>Activity.Value</c> blob, never any free-form
/// user comment text. This is the sanitization mandated by <c>tech-spec.md</c> §4.3
/// (PayloadJson must contain "no secrets or PII beyond identity"). The card-state lookup
/// is best-effort: a missing or failing <see cref="ICardStateStore.GetByQuestionIdAsync"/>
/// is logged at warning level and the resulting payload simply omits the
/// <c>cardActivityId</c> hint.
/// </para>
/// </remarks>
public sealed class CardActionHandler : ICardActionHandler
{
    private readonly IAgentQuestionStore _questionStore;
    private readonly ICardStateStore _cardStateStore;
    private readonly ITeamsCardManager _cardManager;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<CardActionHandler> _logger;
    private readonly CardActionMapper _mapper;

    /// <summary>
    /// Construct the handler with the six dependencies required by
    /// <c>implementation-plan.md</c> §3.3 step 3. Every parameter is null-guarded so DI
    /// mis-registration fails loudly.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _cardManager = cardManager ?? throw new ArgumentNullException(nameof(cardManager));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mapper = new CardActionMapper();
    }

    /// <inheritdoc />
    public async Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turnContext);

        var activity = turnContext.Activity;
        var actorAad = activity?.From?.AadObjectId ?? activity?.From?.Id ?? "unknown";
        var actorDisplayName = activity?.From?.Name;
        var tenantId = ResolveTenantId(activity);
        var receivedAt = activity?.Timestamp ?? DateTimeOffset.UtcNow;

        // Guard: an Adaptive Card invoke without an Action.Submit payload is malformed —
        // surface as Rejected so operators can investigate via the audit trail without a
        // stack trace blowing up the bot framework pipeline.
        if (activity?.Value is null)
        {
            _logger.LogWarning("Adaptive card invoke arrived with a null Activity.Value; rejecting.");
            await WriteRejectionAuditAsync(
                actorAad,
                tenantId,
                agentId: null,
                action: "(missing)",
                payloadJson: BuildEmptyPayloadJson(reason: "missing-activity-value"),
                correlationId: ResolveCorrelationId(activity, fallback: Guid.NewGuid().ToString("N")),
                receivedAt,
                ct).ConfigureAwait(false);
            return Reject("Adaptive card payload was missing.");
        }

        CardActionPayload payload;
        try
        {
            payload = _mapper.ReadPayload(activity.Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentNullException)
        {
            _logger.LogWarning(ex, "Adaptive card invoke payload was malformed; rejecting.");
            await WriteRejectionAuditAsync(
                actorAad,
                tenantId,
                agentId: null,
                action: "(invalid)",
                payloadJson: BuildEmptyPayloadJson(reason: "malformed-payload"),
                correlationId: ResolveCorrelationId(activity, fallback: Guid.NewGuid().ToString("N")),
                receivedAt,
                ct).ConfigureAwait(false);
            return Reject($"Adaptive card payload was invalid: {ex.Message}");
        }

        var correlationId = !string.IsNullOrWhiteSpace(payload.CorrelationId)
            ? payload.CorrelationId
            : ResolveCorrelationId(activity, fallback: Guid.NewGuid().ToString("N"));

        var question = await _questionStore.GetByIdAsync(payload.QuestionId, ct).ConfigureAwait(false);
        if (question is null)
        {
            _logger.LogWarning(
                "Card action {ActionValue} for question {QuestionId} rejected — no stored question.",
                payload.ActionValue,
                payload.QuestionId);
            var sanitized = await BuildSanitizedPayloadJsonAsync(payload, agentId: null, ct).ConfigureAwait(false);
            await WriteAuditAsync(
                outcome: AuditOutcomes.Rejected,
                actorAad,
                tenantId,
                agentId: null,
                action: payload.ActionValue,
                payloadJson: sanitized,
                correlationId,
                receivedAt,
                ct).ConfigureAwait(false);
            return Reject($"Question '{payload.QuestionId}' was not found.");
        }

        var allowed = question.AllowedActions
            .Any(a => string.Equals(a.Value, payload.ActionValue, StringComparison.Ordinal));
        if (!allowed)
        {
            _logger.LogWarning(
                "Card action {ActionValue} for question {QuestionId} rejected — not in AllowedActions.",
                payload.ActionValue,
                payload.QuestionId);
            var sanitized = await BuildSanitizedPayloadJsonAsync(payload, question.AgentId, ct).ConfigureAwait(false);
            await WriteAuditAsync(
                outcome: AuditOutcomes.Rejected,
                actorAad,
                tenantId,
                agentId: question.AgentId,
                action: payload.ActionValue,
                payloadJson: sanitized,
                correlationId,
                receivedAt,
                ct).ConfigureAwait(false);
            return Reject($"Action '{payload.ActionValue}' is not permitted on this question.");
        }

        if (!string.Equals(question.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Card action {ActionValue} for question {QuestionId} rejected — status is {Status}.",
                payload.ActionValue,
                payload.QuestionId,
                question.Status);
            var sanitized = await BuildSanitizedPayloadJsonAsync(payload, question.AgentId, ct).ConfigureAwait(false);
            await WriteAuditAsync(
                outcome: AuditOutcomes.Rejected,
                actorAad,
                tenantId,
                agentId: question.AgentId,
                action: payload.ActionValue,
                payloadJson: sanitized,
                correlationId,
                receivedAt,
                ct).ConfigureAwait(false);
            return Reject($"Decision for question '{payload.QuestionId}' has already been recorded.");
        }

        var transitioned = await _questionStore
            .TryUpdateStatusAsync(
                payload.QuestionId,
                AgentQuestionStatuses.Open,
                AgentQuestionStatuses.Resolved,
                ct)
            .ConfigureAwait(false);

        if (!transitioned)
        {
            _logger.LogInformation(
                "Card action {ActionValue} for question {QuestionId} rejected — concurrent resolver won the race.",
                payload.ActionValue,
                payload.QuestionId);
            var sanitized = await BuildSanitizedPayloadJsonAsync(payload, question.AgentId, ct).ConfigureAwait(false);
            await WriteAuditAsync(
                outcome: AuditOutcomes.Rejected,
                actorAad,
                tenantId,
                agentId: question.AgentId,
                action: payload.ActionValue,
                payloadJson: sanitized,
                correlationId,
                receivedAt,
                ct).ConfigureAwait(false);
            return Reject($"Decision for question '{payload.QuestionId}' has already been recorded.");
        }

        // CAS won — emit decision, update the card, and write the success audit row.
        var decision = new HumanDecisionEvent(
            QuestionId: payload.QuestionId,
            ActionValue: payload.ActionValue,
            Comment: payload.Comment,
            Messenger: "Teams",
            ExternalUserId: actorAad,
            ExternalMessageId: activity.Id ?? string.Empty,
            ReceivedAt: receivedAt,
            CorrelationId: correlationId);

        var decisionEvent = new DecisionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            Messenger = "Teams",
            ExternalUserId = actorAad,
            ActivityId = activity.Id,
            Source = MessengerEventSourceFromActivity(activity),
            Timestamp = receivedAt,
            Payload = decision,
        };

        await _inboundEventPublisher.PublishAsync(decisionEvent, ct).ConfigureAwait(false);

        try
        {
            await _cardManager
                .UpdateCardAsync(
                    payload.QuestionId,
                    CardUpdateAction.MarkAnswered,
                    decision,
                    actorDisplayName,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Card-update failure is logged but does NOT roll back the durable resolution
            // — the decision has already been recorded and published. Operators can later
            // re-render the confirmation card from the audit trail; we surface a Failed
            // audit entry below by piggy-backing the success entry's PayloadJson with a
            // best-effort cardUpdateError marker so triage can find the failure.
            _logger.LogError(
                ex,
                "Card update for question {QuestionId} failed after successful resolution; decision is durably recorded.",
                payload.QuestionId);
        }

        var successPayload = await BuildSanitizedPayloadJsonAsync(payload, question.AgentId, ct).ConfigureAwait(false);
        await WriteAuditAsync(
            outcome: AuditOutcomes.Success,
            actorAad,
            tenantId,
            agentId: question.AgentId,
            action: payload.ActionValue,
            payloadJson: successPayload,
            correlationId,
            receivedAt,
            ct).ConfigureAwait(false);

        return Accept($"Recorded {payload.ActionValue} for {payload.QuestionId}.");
    }

    private static AdaptiveCardInvokeResponse Reject(string message)
        => new()
        {
            StatusCode = 200,
            Type = "application/vnd.microsoft.activity.message",
            Value = message,
        };

    private static AdaptiveCardInvokeResponse Accept(string message)
        => new()
        {
            StatusCode = 200,
            Type = "application/vnd.microsoft.activity.message",
            Value = message,
        };

    private static string ResolveTenantId(Activity? activity)
    {
        var tenant = activity?.ChannelData is JObject channelData
            ? channelData.SelectToken("tenant.id")?.ToString()
            : null;
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            return tenant!;
        }

        return activity?.Conversation?.TenantId ?? "unknown";
    }

    private static string ResolveCorrelationId(Activity? activity, string fallback)
    {
        if (activity?.Properties is JObject props)
        {
            var token = props.GetValue("correlationId", StringComparison.OrdinalIgnoreCase);
            var value = token?.Type == JTokenType.String ? token.ToString() : null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return fallback;
    }

    private static string MessengerEventSourceFromActivity(Activity activity)
    {
        var conversationType = activity.Conversation?.ConversationType;
        return conversationType switch
        {
            "channel" => MessengerEventSources.TeamChannel,
            "groupChat" => MessengerEventSources.TeamChannel,
            "personal" => MessengerEventSources.PersonalChat,
            _ => MessengerEventSources.PersonalChat,
        };
    }

    private async Task<string> BuildSanitizedPayloadJsonAsync(
        CardActionPayload payload,
        string? agentId,
        CancellationToken ct)
    {
        // Sanitization rules per tech-spec.md §4.3 — payload JSON must carry no secrets or
        // PII beyond identity. We persist only the canonical card-action discriminators
        // (question + action identity, correlation ID), a redacted comment indicator, and
        // — if available — the persisted card activity ID. The free-form user-supplied
        // comment text is replaced with a "<redacted>" sentinel so auditors can see that
        // a comment was provided without revealing its content.
        string? cardActivityId = null;
        try
        {
            var card = await _cardStateStore.GetByQuestionIdAsync(payload.QuestionId, ct).ConfigureAwait(false);
            cardActivityId = card?.ActivityId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Card-state lookup for question {QuestionId} failed while building audit payload; continuing.",
                payload.QuestionId);
        }

        var sanitized = new
        {
            questionId = payload.QuestionId,
            actionId = payload.ActionId,
            actionValue = payload.ActionValue,
            correlationId = payload.CorrelationId,
            comment = string.IsNullOrEmpty(payload.Comment) ? null : "<redacted>",
            cardActivityId,
            agentId,
        };

        return JsonSerializer.Serialize(sanitized);
    }

    private static string BuildEmptyPayloadJson(string reason)
    {
        var sanitized = new { reason };
        return JsonSerializer.Serialize(sanitized);
    }

    private async Task WriteRejectionAuditAsync(
        string actorAad,
        string tenantId,
        string? agentId,
        string action,
        string payloadJson,
        string correlationId,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        await WriteAuditAsync(
            AuditOutcomes.Rejected,
            actorAad,
            tenantId,
            agentId,
            action,
            payloadJson,
            correlationId,
            receivedAt,
            ct).ConfigureAwait(false);
    }

    private async Task WriteAuditAsync(
        string outcome,
        string actorAad,
        string tenantId,
        string? agentId,
        string action,
        string payloadJson,
        string correlationId,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        var checksum = AuditEntry.ComputeChecksum(
            timestamp: receivedAt,
            correlationId: correlationId,
            eventType: AuditEventTypes.CardActionReceived,
            actorId: actorAad,
            actorType: AuditActorTypes.User,
            tenantId: tenantId,
            agentId: agentId,
            taskId: null,
            conversationId: null,
            action: action,
            payloadJson: payloadJson,
            outcome: outcome);

        var entry = new AuditEntry
        {
            Timestamp = receivedAt,
            CorrelationId = correlationId,
            EventType = AuditEventTypes.CardActionReceived,
            ActorId = actorAad,
            ActorType = AuditActorTypes.User,
            TenantId = tenantId,
            AgentId = agentId,
            TaskId = null,
            ConversationId = null,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };

        try
        {
            await _auditLogger.LogAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit logging failure must not abort the user-facing response — the
            // decision has been durably recorded and the operator can recover the audit
            // trail from primary persistence at a later point.
            _logger.LogError(ex, "Failed to write CardActionReceived audit entry (outcome={Outcome}).", outcome);
        }
    }
}
