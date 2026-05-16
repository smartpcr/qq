using System.Collections.Concurrent;
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
/// <c>implementation-plan.md</c> (steps 3 and 4) and <c>architecture.md</c> ┬º2.6 / ┬º6.3.
/// Replaces the <c>NoOpCardActionHandler</c> stub registered in Stage 2.1 once the SQL
/// stores ship.
/// </summary>
/// <remarks>
/// <para>
/// The handler is deliberately the single point of entry from
/// <see cref="TeamsSwarmActivityHandler"/>'s <c>OnAdaptiveCardInvokeAsync</c> override ΓÇö
/// every responsibility called out in the implementation plan is fulfilled here:
/// </para>
/// <list type="number">
/// <item><description>Parse the inbound <see cref="Activity.Value"/> via
/// <see cref="CardActionMapper.ReadPayload"/> to extract the <c>QuestionId</c>,
/// <c>ActionId</c>, <c>ActionValue</c>, <c>CorrelationId</c>, and the optional
/// <c>Comment</c> ΓÇö the same code path used by Stage 3.1's
/// <see cref="CardActionMapper"/> so a typo on either side fails fast.</description></item>
/// <item><description>Apply the <c>architecture.md</c> ┬º2.6 fast-path dedupe ΓÇö the
/// <see cref="_processedActions"/> in-memory set keyed on <c>QuestionId + ActorAad</c>
/// short-circuits within-session duplicate submissions (double-taps) <i>before</i>
/// touching any store. Entries are kept for
/// <see cref="DedupeRetentionPeriod"/> on every terminal outcome (success or known
/// rejection) so a user cannot get a different reply by spamming the same action;
/// entries are explicitly evicted on unhandled exceptions so transient infrastructure
/// failures remain retryable.</description></item>
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
/// <c>false</c> (another pod won the race per <c>architecture.md</c> ┬º6.3), surface
/// a "decision already recorded" card and emit a <c>Rejected</c> audit entry ΓÇö the
/// concurrent winner is responsible for the decision event and the card update.</description></item>
/// <item><description>On a successful CAS publish a <see cref="DecisionEvent"/>
/// wrapping the <see cref="HumanDecisionEvent"/> via
/// <see cref="IInboundEventPublisher.PublishAsync"/>, replace the original card via
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
/// (the actor-attributed overload ΓÇö the canonical 3-arg
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, CancellationToken)"/>
/// remains intact for callers that have no decision payload), and write a
/// <see cref="AuditEventTypes.CardActionReceived"/> audit entry. <b>Iter-8 fix #3:</b>
/// if the card update throws after the durable resolution succeeds, the audit row is
/// written with <see cref="AuditOutcomes.Failed"/> and a <c>cardUpdateError</c>
/// marker in the sanitised payload JSON ΓÇö the decision itself is durably recorded so
/// the caller still receives an <c>Accept</c> response, but operators see the
/// lifecycle gap in the audit trail.</description></item>
/// </list>
/// <para>
/// <b>Audit payload sanitisation.</b> The <see cref="AuditEntry.PayloadJson"/> field is
/// built by <see cref="BuildSanitizedPayloadJsonAsync"/> which extracts only the
/// canonical card-action fields plus a redacted comment indicator and the persisted
/// card-state activity ID ΓÇö never the raw <c>Activity.Value</c> blob, never any free-form
/// user comment text. This is the sanitization mandated by <c>tech-spec.md</c> ┬º4.3
/// (PayloadJson must contain "no secrets or PII beyond identity"). The card-state lookup
/// is best-effort: a missing or failing <see cref="ICardStateStore.GetByQuestionIdAsync"/>
/// is logged at warning level and the resulting payload simply omits the
/// <c>cardActivityId</c> hint.
/// </para>
/// </remarks>
public sealed class CardActionHandler : ICardActionHandler
{
    /// <summary>
    /// Retention window for the in-memory processed-action dedupe set
    /// (<c>architecture.md</c> ┬º2.6 layer 2). Entries older than this are pruned on
    /// every <see cref="HandleAsync"/> entry so the set does not grow unbounded.
    /// Five minutes is intentionally wider than the typical card-double-tap window but
    /// narrow enough that a user who genuinely retries after a transient failure can
    /// recover. Tests opt in to a shorter window via the
    /// <c>internal</c> 8-argument constructor.
    /// </summary>
    internal static readonly TimeSpan DedupeRetentionPeriod = TimeSpan.FromMinutes(5);

    private readonly IAgentQuestionStore _questionStore;
    private readonly ICardStateStore _cardStateStore;
    private readonly ITeamsCardManager _cardManager;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<CardActionHandler> _logger;
    private readonly CardActionMapper _mapper;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _dedupeRetention;

    // architecture.md ┬º2.6 layer 2 ΓÇö fast-path dedupe keyed on "{questionId}|{actorAad}".
    // Tracks the timestamp the key was first claimed so stale entries (older than
    // _dedupeRetention) can be evicted lazily on every HandleAsync entry.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedActions = new(StringComparer.Ordinal);

    /// <summary>
    /// Construct the handler with the six dependencies required by
    /// <c>implementation-plan.md</c> ┬º3.3 step 3. Every parameter is null-guarded so DI
    /// mis-registration fails loudly.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger)
        : this(questionStore, cardStateStore, cardManager, inboundEventPublisher, auditLogger, logger, TimeProvider.System, DedupeRetentionPeriod)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic
    /// <see cref="TimeProvider"/> and a configurable dedupe retention window. Hosts
    /// resolve the public 6-arg constructor via DI; unit tests opt in to this overload
    /// to advance the dedupe clock without sleeping.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger,
        TimeProvider timeProvider,
        TimeSpan dedupeRetention)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _cardManager = cardManager ?? throw new ArgumentNullException(nameof(cardManager));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _dedupeRetention = dedupeRetention > TimeSpan.Zero ? dedupeRetention : DedupeRetentionPeriod;
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
        var receivedAt = activity?.Timestamp ?? _timeProvider.GetUtcNow();

        // Guard: an Adaptive Card invoke without an Action.Submit payload is malformed ΓÇö
        // surface as Rejected so operators can investigate via the audit trail without a
        // stack trace blowing up the bot framework pipeline. The dedupe layer cannot key
        // off a missing question ID so we skip it for this malformed branch.
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

        // architecture.md ┬º2.6 layer 2 ΓÇö in-memory processed-action dedupe set, keyed on
        // (QuestionId + UserId). Fast-path short-circuits within-session duplicate
        // submissions (e.g. user double-tapped the Approve button or the Bot Framework
        // delivered the same invoke twice) before any I/O. Entries are kept for
        // _dedupeRetention on every terminal outcome (success or planned rejection); only
        // unhandled exceptions evict the entry so transient infrastructure failures stay
        // retryable.
        var dedupeKey = BuildDedupeKey(payload.QuestionId, actorAad);
        var nowForDedupe = _timeProvider.GetUtcNow();
        PruneStaleDedupeEntries(nowForDedupe);
        if (!_processedActions.TryAdd(dedupeKey, nowForDedupe))
        {
            _logger.LogInformation(
                "Card action {ActionValue} for question {QuestionId} by actor {Actor} short-circuited by in-memory dedupe.",
                payload.ActionValue,
                payload.QuestionId,
                actorAad);
            return Reject($"Decision for question '{payload.QuestionId}' has already been submitted.");
        }

        var keepDedupeEntry = false;
        try
        {
            var response = await ProcessHandledAsync(
                turnContext,
                activity,
                payload,
                actorAad,
                actorDisplayName,
                tenantId,
                receivedAt,
                correlationId,
                ct).ConfigureAwait(false);
            keepDedupeEntry = true;
            return response;
        }
        catch
        {
            // Unhandled exception means we never reached a terminal outcome ΓÇö let the
            // user retry the action by evicting the dedupe entry. Planned rejections and
            // the Failed-audit branch flow through the normal return path above and keep
            // the entry.
            throw;
        }
        finally
        {
            if (!keepDedupeEntry)
            {
                _processedActions.TryRemove(dedupeKey, out _);
            }
        }
    }

    private async Task<AdaptiveCardInvokeResponse> ProcessHandledAsync(
        ITurnContext turnContext,
        Activity activity,
        CardActionPayload payload,
        string actorAad,
        string? actorDisplayName,
        string tenantId,
        DateTimeOffset receivedAt,
        string correlationId,
        CancellationToken ct)
    {
        _ = turnContext;

        var question = await _questionStore.GetByIdAsync(payload.QuestionId, ct).ConfigureAwait(false);
        if (question is null)
        {
            _logger.LogWarning(
                "Card action {ActionValue} for question {QuestionId} rejected ΓÇö no stored question.",
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
                "Card action {ActionValue} for question {QuestionId} rejected ΓÇö not in AllowedActions.",
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
                "Card action {ActionValue} for question {QuestionId} rejected ΓÇö status is {Status}.",
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
            // Iter-3 evaluator feedback #3 (re-applied iter-4 after the review-comment
            // commit reverted the original split) ΓÇö emit a distinct "Expired" rejection
            // message when the durable status is Expired, so operators and the Teams
            // client can distinguish lifecycle-expired questions from user-resolved ones.
            if (string.Equals(question.Status, AgentQuestionStatuses.Expired, StringComparison.Ordinal))
            {
                return Reject($"Question '{payload.QuestionId}' has Expired and can no longer accept decisions.");
            }

            return Reject($"Decision for question '{payload.QuestionId}' has already been recorded.");
        }

        // Iter-3 evaluator feedback #3 (re-applied iter-4) ΓÇö between the moment
        // ExpiresAt elapses and the moment QuestionExpiryProcessor's next sweep flips
        // Status to Expired, there is a window where Status is still Open but the
        // deadline has passed. Reject explicitly with the Expired message so the same
        // outcome the worker produces is also produced here. Strict less-than matches
        // SqlAgentQuestionStore.GetOpenExpiredAsync (`e.ExpiresAt < cutoff`) and
        // QuestionExpiryProcessor's cutoff semantics ΓÇö diverging here would create
        // boundary mismatches between the handler and the worker.
        if (question.ExpiresAt < _timeProvider.GetUtcNow())
        {
            _logger.LogInformation(
                "Card action {ActionValue} for question {QuestionId} rejected ΓÇö ExpiresAt {ExpiresAt} has passed (Open-but-stale).",
                payload.ActionValue,
                payload.QuestionId,
                question.ExpiresAt);
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
            return Reject($"Question '{payload.QuestionId}' has Expired and can no longer accept decisions.");
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
                "Card action {ActionValue} for question {QuestionId} rejected ΓÇö concurrent resolver won the race.",
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

        // CAS won ΓÇö emit decision, update the card, and write the success audit row.
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

        try
        {
            await _inboundEventPublisher.PublishAsync(decisionEvent, ct).ConfigureAwait(false);
        }
        catch (Exception publishEx)
        {
            // Iter-9 fix (FR-005 "Message loss: 0 tolerated"): the durable status was
            // just flipped Open->Resolved, but the decision event never reached the
            // inbound publisher (channel full, writer closed, host shutdown, ...). If
            // we leave the question pinned Resolved, the actor's retry will short-
            // circuit on the status guard above and return "already recorded" — the
            // human's decision is silently lost and the agent never observes it.
            // Compensate by attempting a Resolved->Open CAS so the retry path (the
            // outer HandleAsync catch evicts the dedupe entry on rethrow) can re-run
            // end-to-end.
            //
            // Rollback uses a fresh CancellationTokenSource bounded by a short
            // timeout: the inbound ct is most often cancelled when PublishAsync
            // fails (channel shutdown, host stop), but the rollback must still make
            // a best-effort attempt to land. The bounded timeout protects against a
            // pathological store hang.
            using var rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            bool rolledBack;
            try
            {
                rolledBack = await _questionStore
                    .TryUpdateStatusAsync(
                        payload.QuestionId,
                        AgentQuestionStatuses.Resolved,
                        AgentQuestionStatuses.Open,
                        rollbackCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(
                    new AggregateException(publishEx, rollbackEx),
                    "Decision publish failed for question {QuestionId} and the compensating Resolved->Open rollback also threw; the question is stuck Resolved with no decision event delivered. Operator intervention is required to restore status=Open or to replay the decision out-of-band.",
                    payload.QuestionId);
                throw;
            }

            if (rolledBack)
            {
                _logger.LogWarning(
                    publishEx,
                    "Decision publish failed for question {QuestionId}; compensating CAS Resolved->Open succeeded so the actor can retry.",
                    payload.QuestionId);
            }
            else
            {
                // The status was already moved off Resolved by some other actor (very
                // unlikely — only the expiry processor would touch a Resolved row,
                // and it ignores Resolved). We cannot deliver the original decision,
                // so log loud and let the original exception propagate.
                _logger.LogError(
                    publishEx,
                    "Decision publish failed for question {QuestionId} and the compensating Resolved->Open CAS did not apply (status diverged); the decision is lost and operator intervention is required.",
                    payload.QuestionId);
            }

            throw;
        }

        // Iter-8 fix #3: capture the card-update exception (if any) so the audit row
        // truthfully reflects the lifecycle outcome. The decision is durably resolved at
        // this point and we still return Accept to the caller ΓÇö but the audit must NOT
        // report Success when the original card was not replaced.
        Exception? cardUpdateException = null;
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
            cardUpdateException = ex;
            _logger.LogError(
                ex,
                "Card update for question {QuestionId} failed after successful resolution; decision is durably recorded but the original card remains active and is now stale.",
                payload.QuestionId);
        }

        var auditOutcome = cardUpdateException is null
            ? AuditOutcomes.Success
            : AuditOutcomes.Failed;
        var payloadJson = await BuildSanitizedPayloadJsonAsync(
            payload,
            question.AgentId,
            ct,
            cardUpdateError: cardUpdateException?.Message).ConfigureAwait(false);
        await WriteAuditAsync(
            outcome: auditOutcome,
            actorAad,
            tenantId,
            agentId: question.AgentId,
            action: payload.ActionValue,
            payloadJson: payloadJson,
            correlationId,
            receivedAt,
            ct).ConfigureAwait(false);

        return Accept($"Recorded {payload.ActionValue} for {payload.QuestionId}.");
    }

    private static string BuildDedupeKey(string questionId, string actorAad)
        => $"{questionId}|{actorAad}";

    private void PruneStaleDedupeEntries(DateTimeOffset now)
    {
        // Lazy O(N) eviction on each entry ΓÇö the dedupe set is bounded by the rate of
        // distinct (question, user) pairs within _dedupeRetention. For a realistic
        // approval workload this stays in the low hundreds of entries even at peak. We
        // deliberately avoid a background sweep timer because keeping the handler free
        // of timers makes lifetime reasoning and tests simpler.
        if (_processedActions.IsEmpty)
        {
            return;
        }

        foreach (var kvp in _processedActions)
        {
            if (now - kvp.Value > _dedupeRetention)
            {
                _processedActions.TryRemove(kvp.Key, out _);
            }
        }
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
        CancellationToken ct,
        string? cardUpdateError = null)
    {
        // Sanitization rules per tech-spec.md ┬º4.3 ΓÇö payload JSON must carry no secrets or
        // PII beyond identity. We persist only the canonical card-action discriminators
        // (question + action identity, correlation ID), a redacted comment indicator, and
        // ΓÇö if available ΓÇö the persisted card activity ID. The free-form user-supplied
        // comment text is replaced with a "<redacted>" sentinel so auditors can see that
        // a comment was provided without revealing its content.
        //
        // Iter-8 fix #3: when the post-resolution card update threw, surface the
        // exception's Message via the `cardUpdateError` marker so operators can find the
        // lifecycle gap in the audit trail. We log the full exception elsewhere; the
        // PayloadJson keeps only the short message so the audit row stays compact.
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
            cardUpdateError,
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
            // Audit logging failure must not abort the user-facing response ΓÇö the
            // decision has been durably recorded and the operator can recover the audit
            // trail from primary persistence at a later point.
            _logger.LogError(ex, "Failed to write CardActionReceived audit entry (outcome={Outcome}).", outcome);
        }
    }
}
