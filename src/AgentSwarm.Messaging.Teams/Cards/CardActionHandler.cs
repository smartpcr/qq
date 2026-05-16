using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Concrete <see cref="ICardActionHandler"/> implementation introduced in Stage 3.3.
/// Parses an inbound Adaptive Card <c>adaptiveCard/action</c> invoke, validates the
/// chosen action against the originating <see cref="AgentQuestion.AllowedActions"/>,
/// transitions the question's <see cref="AgentQuestion.Status"/> via the
/// compare-and-set <see cref="IAgentQuestionStore.TryUpdateStatusAsync"/> (first-writer
/// wins), publishes a <see cref="DecisionEvent"/> wrapping the
/// <see cref="HumanDecisionEvent"/>, and replaces the original card via
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>.
/// Every terminal path writes a single sanitised <see cref="AuditEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dedupe (architecture §2.6 layer 2).</b> A per-actor / per-question / per-action
/// in-process dedupe set short-circuits duplicate submissions within
/// <see cref="DedupeTtl"/> so a Teams double-tap cannot produce two terminal outcomes or
/// two decision events. The dedupe key is <c>(QuestionId, AadObjectId, ActionValue)</c>
/// — a different actor (or different action) is NOT deduped. When the handler throws
/// (infrastructure outage), the dedupe entry is evicted in the <c>finally</c> so the
/// actor can retry.
/// </para>
/// <para>
/// <b>Sanitised audit payload.</b> The user-supplied <see cref="HumanDecisionEvent.Comment"/>
/// is replaced with the literal sentinel <c>&lt;redacted&gt;</c> before serialisation so
/// free-text comments cannot leak PII or secrets into the audit log. The audit payload
/// captures only stable, non-sensitive context — the question / action IDs, the
/// correlation ID, the actor's AAD object ID, the (optional) Teams activity ID resolved
/// from <see cref="ICardStateStore"/>, and whether a comment was present (without the
/// content).
/// </para>
/// <para>
/// <b>Update-card failure handling.</b> When
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
/// throws AFTER the durable Open→Resolved CAS succeeded, the durable resolution stands
/// (the user's choice is recorded and the decision event is published) but the audit
/// outcome flips to <see cref="AuditOutcomes.Failed"/> with a <c>cardUpdateError</c>
/// marker — operators need a signal that the user-visible card was not replaced.
/// </para>
/// </remarks>
public sealed class CardActionHandler : ICardActionHandler
{
    private const string AdaptiveCardInvokeName = "adaptiveCard/action";
    private const string MessageResponseType = "application/vnd.microsoft.activity.message";
    private const string ErrorResponseType = "application/vnd.microsoft.error";
    private const string TeamsMessengerLabel = "Teams";
    private const string RedactionSentinel = "<redacted>";

    /// <summary>Window inside which a repeated (actor, question, action) tap is treated as a double-tap.</summary>
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions InvokePayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions SanitisedPayloadJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAgentQuestionStore _questionStore;
    private readonly ICardStateStore _cardStateStore;
    private readonly ITeamsCardManager _cardManager;
    private readonly IInboundEventPublisher _publisher;
    private readonly IAuditLogger _audit;
    private readonly ILogger<CardActionHandler> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<DedupeKey, DateTimeOffset> _dedupe = new();

    /// <summary>
    /// Production constructor — defaults the clock to <see cref="TimeProvider.System"/>.
    /// Every dependency is null-guarded so DI mis-registration fails loudly at the
    /// composition root rather than producing a <see cref="NullReferenceException"/>
    /// inside an inbound invoke.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher publisher,
        IAuditLogger audit,
        ILogger<CardActionHandler> logger)
        : this(questionStore, cardStateStore, cardManager, publisher, audit, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic <see cref="TimeProvider"/>
    /// so unit tests can pin audit timestamps and exercise dedupe TTL without
    /// wall-clock flakiness.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher publisher,
        IAuditLogger audit,
        ILogger<CardActionHandler> logger,
        TimeProvider timeProvider)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _cardManager = cardManager ?? throw new ArgumentNullException(nameof(cardManager));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<AdaptiveCardInvokeResponse> HandleAsync(ITurnContext turnContext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turnContext);

        var activity = turnContext.Activity
            ?? throw new InvalidOperationException("ITurnContext.Activity must not be null for an Adaptive Card invoke.");

        var actorAad = activity.From?.AadObjectId ?? activity.From?.Id ?? "unknown";
        var actorDisplayName = activity.From?.Name;
        var tenantId = ResolveTenantId(activity);

        // Parse the payload up front. A null / malformed Activity.Value is a rejection
        // — we still write a Rejected audit row so the security/forensic timeline shows
        // the invalid invoke arrived. CardActionPayload validation is delegated to
        // CardActionMapper.ReadPayload which throws on missing required keys.
        CardActionPayload? payload = null;
        Exception? parseError = null;
        try
        {
            if (activity.Value is null)
            {
                parseError = new InvalidOperationException("Activity.Value is null.");
            }
            else
            {
                payload = new CardActionMapper().ReadPayload(activity.Value);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            parseError = ex;
        }

        if (payload is null)
        {
            await WriteAuditAsync(
                tenantId: tenantId,
                correlationId: activity.GetCorrelationId() ?? Guid.NewGuid().ToString(),
                actorAad: actorAad,
                agentId: null,
                taskId: null,
                conversationId: activity.Conversation?.Id,
                action: "invalid",
                payloadJson: BuildErrorPayload("invalidPayload", parseError?.Message ?? "Payload missing or unparseable."),
                outcome: AuditOutcomes.Rejected,
                ct).ConfigureAwait(false);

            return Reject("InvalidPayload", "Adaptive card payload was missing or malformed.");
        }

        // Architecture §2.6 layer 2: short-circuit duplicate submissions BEFORE touching
        // any store. The dedupe key is (QuestionId, AadObjectId, ActionValue) so a
        // different actor (or a different action) is NOT deduped.
        var dedupeKey = new DedupeKey(payload.QuestionId, actorAad, payload.ActionValue);
        if (TryShortCircuitDuplicate(dedupeKey))
        {
            _logger.LogDebug(
                "Short-circuiting duplicate card action for question {QuestionId}, actor {Actor}, action {Action}.",
                payload.QuestionId, actorAad, payload.ActionValue);
            return Accept();
        }

        var dedupeRegistered = true;
        try
        {
            // GetByIdAsync is intentionally NOT inside a try/catch — an infrastructure
            // outage (store down) should propagate so the caller can retry. The finally
            // block below evicts the dedupe entry so the actor's retry is not silently
            // short-circuited.
            var question = await _questionStore.GetByIdAsync(payload.QuestionId, ct).ConfigureAwait(false);

            if (question is null)
            {
                await WriteAuditAsync(
                    tenantId: tenantId,
                    correlationId: payload.CorrelationId,
                    actorAad: actorAad,
                    agentId: null,
                    taskId: null,
                    conversationId: activity.Conversation?.Id,
                    action: payload.ActionValue,
                    payloadJson: BuildSanitisedPayload(payload, actorAad, activityId: activity.ReplyToId, commentPresent: payload.Comment is not null, cardUpdateError: null),
                    outcome: AuditOutcomes.Rejected,
                    ct).ConfigureAwait(false);

                return Reject("QuestionNotFound", "This question is no longer available.");
            }

            if (!IsActionAllowed(question, payload.ActionValue))
            {
                await WriteAuditAsync(
                    tenantId: tenantId,
                    correlationId: payload.CorrelationId,
                    actorAad: actorAad,
                    agentId: question.AgentId,
                    taskId: question.TaskId,
                    conversationId: activity.Conversation?.Id ?? question.ConversationId,
                    action: payload.ActionValue,
                    payloadJson: BuildSanitisedPayload(payload, actorAad, activityId: activity.ReplyToId, commentPresent: payload.Comment is not null, cardUpdateError: null),
                    outcome: AuditOutcomes.Rejected,
                    ct).ConfigureAwait(false);

                return Reject("InvalidAction", $"'{payload.ActionValue}' is not an allowed action for this question.");
            }

            if (!string.Equals(question.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
            {
                await WriteAuditAsync(
                    tenantId: tenantId,
                    correlationId: payload.CorrelationId,
                    actorAad: actorAad,
                    agentId: question.AgentId,
                    taskId: question.TaskId,
                    conversationId: activity.Conversation?.Id ?? question.ConversationId,
                    action: payload.ActionValue,
                    payloadJson: BuildSanitisedPayload(payload, actorAad, activityId: activity.ReplyToId, commentPresent: payload.Comment is not null, cardUpdateError: null),
                    outcome: AuditOutcomes.Rejected,
                    ct).ConfigureAwait(false);

                return Reject(
                    "AlreadyResolved",
                    $"This question has already been {question.Status.ToLowerInvariant()}.");
            }

            // First-writer-wins CAS. A concurrent winner causes the CAS to return false;
            // we record a Rejected audit row and tell the user.
            var won = await _questionStore
                .TryUpdateStatusAsync(question.QuestionId, AgentQuestionStatuses.Open, AgentQuestionStatuses.Resolved, ct)
                .ConfigureAwait(false);
            if (!won)
            {
                await WriteAuditAsync(
                    tenantId: tenantId,
                    correlationId: payload.CorrelationId,
                    actorAad: actorAad,
                    agentId: question.AgentId,
                    taskId: question.TaskId,
                    conversationId: activity.Conversation?.Id ?? question.ConversationId,
                    action: payload.ActionValue,
                    payloadJson: BuildSanitisedPayload(payload, actorAad, activityId: activity.ReplyToId, commentPresent: payload.Comment is not null, cardUpdateError: null),
                    outcome: AuditOutcomes.Rejected,
                    ct).ConfigureAwait(false);

                return Reject("RaceLost", "This question was just resolved by someone else.");
            }

            // Build & publish the decision payload. Comment passes through here intact
            // (it goes onto the in-process channel, NOT into the audit log).
            var decision = new HumanDecisionEvent(
                QuestionId: question.QuestionId,
                ActionValue: payload.ActionValue,
                Comment: payload.Comment,
                Messenger: TeamsMessengerLabel,
                ExternalUserId: actorAad,
                ExternalMessageId: activity.Id ?? Guid.NewGuid().ToString("N"),
                ReceivedAt: _timeProvider.GetUtcNow(),
                CorrelationId: payload.CorrelationId);

            var decisionEvent = new DecisionEvent
            {
                EventId = Guid.NewGuid().ToString(),
                CorrelationId = payload.CorrelationId,
                Messenger = TeamsMessengerLabel,
                ExternalUserId = actorAad,
                ActivityId = activity.Id,
                Source = activity.Conversation?.ConversationType == "channel"
                    ? MessengerEventSources.TeamChannel
                    : MessengerEventSources.PersonalChat,
                Timestamp = _timeProvider.GetUtcNow(),
                Payload = decision,
            };

            await _publisher.PublishAsync(decisionEvent, ct).ConfigureAwait(false);

            // Best-effort: try to resolve the original activity ID from card state so it
            // can be surfaced in the sanitised audit payload. A throw from the store is
            // swallowed — the audit log MUST be written regardless of whether we know
            // the original activity ID.
            string? resolvedActivityId = activity.ReplyToId;
            try
            {
                var cardState = await _cardStateStore.GetByQuestionIdAsync(question.QuestionId, ct).ConfigureAwait(false);
                if (cardState is not null && !string.IsNullOrWhiteSpace(cardState.ActivityId))
                {
                    resolvedActivityId = cardState.ActivityId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Best-effort card-state lookup failed for question {QuestionId}; continuing.", question.QuestionId);
            }

            // Replace the original card via the decision-attributed overload. A throw
            // here is non-fatal for the durable resolution (CAS already won, decision
            // already published, response will still Accept) but flips the audit outcome
            // to Failed with a cardUpdateError marker so operators can see the visible
            // card was not replaced.
            string? cardUpdateError = null;
            try
            {
                await _cardManager
                    .UpdateCardAsync(question.QuestionId, CardUpdateAction.MarkAnswered, decision, actorDisplayName, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cardUpdateError = ex.Message;
                _logger.LogError(ex, "Card update failed for question {QuestionId}; audit will reflect Failed.", question.QuestionId);
            }

            await WriteAuditAsync(
                tenantId: tenantId,
                correlationId: payload.CorrelationId,
                actorAad: actorAad,
                agentId: question.AgentId,
                taskId: question.TaskId,
                conversationId: activity.Conversation?.Id ?? question.ConversationId,
                action: payload.ActionValue,
                payloadJson: BuildSanitisedPayload(
                    payload,
                    actorAad,
                    activityId: resolvedActivityId,
                    commentPresent: payload.Comment is not null,
                    cardUpdateError: cardUpdateError),
                outcome: cardUpdateError is null ? AuditOutcomes.Success : AuditOutcomes.Failed,
                ct).ConfigureAwait(false);

            return Accept();
        }
        catch
        {
            // Architecture §2.6: a failure mid-pipeline must evict the dedupe entry so
            // the actor can retry. Without this, a transient outage would permanently
            // reject the actor's next attempt within the dedupe TTL.
            _dedupe.TryRemove(dedupeKey, out _);
            dedupeRegistered = false;
            throw;
        }
        finally
        {
            if (dedupeRegistered)
            {
                // Leave the registered entry in place on success — it is the source of
                // truth for "this actor already submitted this action". Eviction on the
                // success path happens lazily via the TTL check in TryShortCircuit.
            }
        }
    }

    private bool TryShortCircuitDuplicate(DedupeKey key)
    {
        var now = _timeProvider.GetUtcNow();

        // If a recent entry exists AND is still within the TTL, the second submission is
        // a duplicate. Otherwise install (or refresh) the entry and run the pipeline.
        if (_dedupe.TryGetValue(key, out var existing) && now - existing < DedupeTtl)
        {
            return true;
        }

        _dedupe[key] = now;
        return false;
    }

    private static bool IsActionAllowed(AgentQuestion question, string actionValue)
    {
        foreach (var action in question.AllowedActions)
        {
            if (string.Equals(action.Value, actionValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveTenantId(Activity activity)
    {
        // Preferred: Conversation.TenantId (set by the Teams channel adapter).
        var convTenant = activity.Conversation?.TenantId;
        if (!string.IsNullOrWhiteSpace(convTenant))
        {
            return convTenant!;
        }

        // Fallback: ChannelData.tenant.id when the activity carries Teams channel data.
        if (activity.ChannelData is Newtonsoft.Json.Linq.JObject channelData)
        {
            var tenantToken = channelData["tenant"]?["id"];
            if (tenantToken is not null)
            {
                var raw = tenantToken.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string?)tenantToken
                    : tenantToken.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw!;
                }
            }
        }

        return "unknown";
    }

    private static AdaptiveCardInvokeResponse Accept()
    {
        return new AdaptiveCardInvokeResponse
        {
            StatusCode = (int)HttpStatusCode.OK,
            Type = MessageResponseType,
            Value = new { message = "Recorded." },
        };
    }

    private static AdaptiveCardInvokeResponse Reject(string code, string message)
    {
        // Bot Framework Universal Action contract: errors use a 4xx status + the
        // application/vnd.microsoft.error type. Value carries a code/message pair the
        // Teams client surfaces to the user.
        return new AdaptiveCardInvokeResponse
        {
            StatusCode = (int)HttpStatusCode.BadRequest,
            Type = ErrorResponseType,
            Value = new { code, message },
        };
    }

    private static string BuildSanitisedPayload(
        CardActionPayload payload,
        string actorAad,
        string? activityId,
        bool commentPresent,
        string? cardUpdateError)
    {
        var sanitised = new SanitisedAuditPayload(
            QuestionId: payload.QuestionId,
            ActionId: payload.ActionId,
            ActionValue: payload.ActionValue,
            CorrelationId: payload.CorrelationId,
            ActorAad: actorAad,
            ActivityId: activityId,
            Comment: commentPresent ? RedactionSentinel : null,
            CardUpdateError: cardUpdateError);
        return JsonSerializer.Serialize(sanitised, SanitisedPayloadJsonOptions);
    }

    private static string BuildErrorPayload(string code, string message)
    {
        return JsonSerializer.Serialize(new { code, message }, SanitisedPayloadJsonOptions);
    }

    private async Task WriteAuditAsync(
        string tenantId,
        string correlationId,
        string actorAad,
        string? agentId,
        string? taskId,
        string? conversationId,
        string action,
        string payloadJson,
        string outcome,
        CancellationToken ct)
    {
        var timestamp = _timeProvider.GetUtcNow();
        const string eventType = AuditEventTypes.CardActionReceived;
        const string actorType = AuditActorTypes.User;

        var checksum = AuditEntry.ComputeChecksum(
            timestamp,
            correlationId,
            eventType,
            actorAad,
            actorType,
            tenantId,
            agentId,
            taskId,
            conversationId,
            action,
            payloadJson,
            outcome);

        var entry = new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = correlationId,
            EventType = eventType,
            ActorId = actorAad,
            ActorType = actorType,
            TenantId = tenantId,
            AgentId = agentId,
            TaskId = taskId,
            ConversationId = conversationId,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };

        await _audit.LogAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Composite key for the dedupe layer. Uses an ordinal comparer so the AAD object
    /// ID's casing is significant (the upstream Teams channel preserves casing).
    /// </summary>
    private readonly record struct DedupeKey(string QuestionId, string ActorAad, string ActionValue);

    /// <summary>Strongly-typed shape for the sanitised audit payload.</summary>
    private sealed record SanitisedAuditPayload(
        string QuestionId,
        string ActionId,
        string ActionValue,
        string CorrelationId,
        string ActorAad,
        string? ActivityId,
        string? Comment,
        string? CardUpdateError);
}

/// <summary>
/// Extension shim around <see cref="Activity"/> for reading the optional
/// <c>correlationId</c> property that <see cref="TeamsSwarmActivityHandler"/> stamps on
/// inbound activities. Kept private to this file so the helper is only available where it
/// is actually used.
/// </summary>
internal static class CardActionHandlerActivityExtensions
{
    public static string? GetCorrelationId(this Activity activity)
    {
        if (activity.Properties is null)
        {
            return null;
        }

        var token = activity.Properties["correlationId"];
        if (token is null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null)
        {
            return null;
        }

        var raw = token.Type == Newtonsoft.Json.Linq.JTokenType.String
            ? (string?)token
            : token.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}
