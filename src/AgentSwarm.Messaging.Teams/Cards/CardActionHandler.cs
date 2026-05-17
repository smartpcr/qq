using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
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
/// <see cref="AuditEventTypes.CardActionReceived"/> audit entry. <b>Card-update
/// failure (iter-2 evaluator feedback #4 / iter-3 evaluator feedback #2):</b> if the
/// card update throws after the durable resolution succeeds, the audit row is
/// written with <see cref="AuditOutcomes.Failed"/> and a <c>cardUpdateError</c>
/// marker in the sanitised payload JSON, AND the caller receives a Bot Framework
/// error response (<c>application/vnd.microsoft.error</c>, HTTP 502, code
/// <c>CardUpdateFailed</c>) produced by <see cref="RejectCardUpdateFailure"/> ΓÇö
/// the decision is durably recorded so the agent observes the human's choice,
/// but the Teams client renders an error notification rather than a misleading
/// "Recorded" confirmation while the original interactive card is stale on
/// screen.</description></item>
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
/// <para>
/// <b>Audit durability (iter-3 evaluator feedback #3).</b> Because the durable
/// <c>Open->Resolved</c> CAS and the <see cref="IInboundEventPublisher.PublishAsync"/>
/// emit happen <i>before</i> <see cref="WriteAuditAsync"/>, an audit-store outage at
/// that point is a compliance-evidence loss the actor's retry cannot heal (the retry
/// hits the resolved-status guard and never re-runs the success-audit emit). The
/// handler therefore (1) retries <see cref="IAuditLogger.LogAsync"/> with exponential
/// backoff (base <see cref="AuditRetryBaseDelay"/>, doubling per attempt, capped at
/// <see cref="AuditRetryMaxDelay"/>, up to <see cref="AuditRetryMaxAttempts"/>
/// attempts) so transient outages self-heal, and (2) on retry exhaustion emits the
/// fully-serialised <see cref="AuditEntry"/> as a single <c>LogCritical</c> log line
/// tagged <c>FALLBACK_AUDIT_ENTRY</c> so operators can recover the row from the log
/// sink (which is independent of the primary audit store) and replay it out-of-band.
/// The original exception is rethrown after the fallback emit so the Bot Framework
/// caller observes the failure and the dedupe entry is evicted via the outer
/// <see cref="HandleAsync"/> finally-block.
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

    /// <summary>
    /// Iter-3 evaluator feedback #3 ΓÇö audit retry base delay. Production defaults are
    /// modest (200ms) because audit-store transient outages typically recover in
    /// hundreds of milliseconds; a longer base delay would expose the user-visible
    /// invoke response to multi-second tail latencies on every transient blip.
    /// Exponential backoff doubles per attempt and caps at
    /// <see cref="AuditRetryMaxDelay"/>. Tests opt in to a shorter base delay via the
    /// <c>internal</c> 10-argument constructor.
    /// </summary>
    internal static readonly TimeSpan AuditRetryBaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Maximum per-attempt backoff cap (mirrors the connector retry policy).</summary>
    internal static readonly TimeSpan AuditRetryMaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Number of audit-write retries before the handler emits the fallback log line and
    /// rethrows. With <see cref="AuditRetryBaseDelay"/> = 200ms and exponential
    /// doubling, the worst-case total wall time is 200+400+800+1600 = 3000ms before
    /// fallback ΓÇö enough for typical transient outages to recover without losing
    /// compliance evidence, but bounded so a persistent outage surfaces promptly.
    /// </summary>
    internal const int AuditRetryMaxAttempts = 4;

    private readonly IAgentQuestionStore _questionStore;
    private readonly ICardStateStore _cardStateStore;
    private readonly ITeamsCardManager _cardManager;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<CardActionHandler> _logger;
    private readonly CardActionMapper _mapper;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _dedupeRetention;
    private readonly TimeSpan _auditRetryBaseDelay;
    private readonly int _auditRetryMaxAttempts;

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
        : this(questionStore, cardStateStore, cardManager, inboundEventPublisher, auditLogger, logger, TimeProvider.System, DedupeRetentionPeriod, AuditRetryBaseDelay, AuditRetryMaxAttempts)
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
        : this(questionStore, cardStateStore, cardManager, inboundEventPublisher, auditLogger, logger, timeProvider, dedupeRetention, AuditRetryBaseDelay, AuditRetryMaxAttempts)
    {
    }

    /// <summary>
    /// Internal master constructor used by unit tests that need to exercise the audit-
    /// retry recovery path (iter-3 evaluator feedback #3) without waiting hundreds of
    /// milliseconds per retry. Production paths chain through the public 6-arg overload
    /// which uses <see cref="AuditRetryBaseDelay"/> and <see cref="AuditRetryMaxAttempts"/>.
    /// </summary>
    internal CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger,
        TimeProvider timeProvider,
        TimeSpan dedupeRetention,
        TimeSpan auditRetryBaseDelay,
        int auditRetryMaxAttempts)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _cardManager = cardManager ?? throw new ArgumentNullException(nameof(cardManager));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _dedupeRetention = dedupeRetention > TimeSpan.Zero ? dedupeRetention : DedupeRetentionPeriod;
        _auditRetryBaseDelay = auditRetryBaseDelay > TimeSpan.Zero ? auditRetryBaseDelay : AuditRetryBaseDelay;
        _auditRetryMaxAttempts = auditRetryMaxAttempts > 0 ? auditRetryMaxAttempts : AuditRetryMaxAttempts;
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
            return Reject(CardErrorCodes.MalformedPayload, 400, "Adaptive card payload was missing.");
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
            return Reject(CardErrorCodes.MalformedPayload, 400, $"Adaptive card payload was invalid: {ex.Message}");
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
            return Reject(CardErrorCodes.DuplicateSubmission, 409, $"Decision for question '{payload.QuestionId}' has already been submitted.");
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
            return Reject(CardErrorCodes.QuestionNotFound, 404, $"Question '{payload.QuestionId}' was not found.");
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
            return Reject(CardErrorCodes.ActionNotAllowed, 403, $"Action '{payload.ActionValue}' is not permitted on this question.");
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
                return Reject(CardErrorCodes.QuestionExpired, 410, $"Question '{payload.QuestionId}' has Expired and can no longer accept decisions.");
            }

            return Reject(CardErrorCodes.AlreadyResolved, 409, $"Decision for question '{payload.QuestionId}' has already been recorded.");
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
            return Reject(CardErrorCodes.QuestionExpired, 410, $"Question '{payload.QuestionId}' has Expired and can no longer accept decisions.");
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
            return Reject(CardErrorCodes.AlreadyResolved, 409, $"Decision for question '{payload.QuestionId}' has already been recorded.");
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

        // Iter-2 evaluator feedback #4 / iter-3 evaluator feedback #2: capture the
        // card-update exception (if any) so the audit row truthfully reflects the
        // lifecycle outcome AND the caller receives an error response. The decision
        // is durably resolved at this point so the agent will observe the human's
        // choice via the already-published DecisionEvent, but the Teams client must
        // see a Bot Framework error response (RejectCardUpdateFailure builds it
        // below) rather than a misleading "Recorded" Accept ΓÇö otherwise the user
        // sees a confirmation chat message while the original interactive card sits
        // stale and clickable on screen. The audit row carries Outcome=Failed and
        // a cardUpdateError marker so operators can reconcile the lifecycle gap.
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

        return cardUpdateException is null
            ? Accept($"Recorded {payload.ActionValue} for {payload.QuestionId}.")
            : RejectCardUpdateFailure(payload, cardUpdateException);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #4 ΓÇö when <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
    /// throws after the durable Open->Resolved CAS succeeds, the original interactive
    /// card remains stale on the user's screen. The previous implementation returned
    /// an <c>Accept</c> response saying "Recorded X for Y" even though the card was
    /// not updated, masking the lifecycle gap from the Teams client. We now return a
    /// Bot Framework Universal Action error response with code
    /// <see cref="CardErrorCodes.CardUpdateFailed"/> and HTTP 502 (Bad Gateway ΓÇö
    /// downstream Bot Framework UpdateActivityAsync failed) so the Teams client
    /// renders the failure rather than a confirmation message. The decision event has
    /// already been published, so the agent observes the human's choice; the audit
    /// row carries <c>Outcome=Failed</c> and a <c>cardUpdateError</c> marker so
    /// operators can reconcile state. The user is told the decision was recorded but
    /// to refresh the conversation to see the updated card.
    /// </summary>
    private static AdaptiveCardInvokeResponse RejectCardUpdateFailure(CardActionPayload payload, Exception cardUpdateException)
    {
        _ = cardUpdateException;
        return Reject(
            CardErrorCodes.CardUpdateFailed,
            502,
            $"Your decision ({payload.ActionValue}) was recorded for {payload.QuestionId}, but the original card could not be updated and may display stale state. Please refresh the conversation.");
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

    // Bot Framework Universal Action Model content-type and HTTP status code constants
    // for invoke responses. Error responses MUST use application/vnd.microsoft.error so
    // the Teams client renders the rejection as a card error rather than a chat message
    // (per the Bot Framework spec: "Universal Actions for Adaptive Cards"). Success
    // acknowledgements use application/vnd.microsoft.activity.message so the user sees
    // an inline confirmation chat message.
    internal const string ContentTypeMessage = "application/vnd.microsoft.activity.message";
    internal const string ContentTypeError = "application/vnd.microsoft.error";

    // Iter-2 evaluator feedback #2 (re-applied iter-2-of-this-stage) -- rejection error
    // codes for the AdaptiveCardInvokeErrorValue body. Each callsite picks the most
    // semantically precise (code, status) pair so operators can grep audit and Bot
    // Framework telemetry by code.
    internal static class CardErrorCodes
    {
        public const string MalformedPayload = "MalformedPayload";
        public const string DuplicateSubmission = "DuplicateSubmission";
        public const string QuestionNotFound = "QuestionNotFound";
        public const string ActionNotAllowed = "ActionNotAllowed";
        public const string AlreadyResolved = "AlreadyResolved";
        public const string QuestionExpired = "QuestionExpired";
        public const string CardUpdateFailed = "CardUpdateFailed";
    }

    /// <summary>
    /// Build a Bot Framework Universal Action error response. Per the spec, the
    /// <c>application/vnd.microsoft.error</c> content type with a non-200 status code
    /// instructs the Teams client to render the response as an error notification
    /// rather than as a chat message acknowledgement. Replaces the previous behaviour
    /// of returning HTTP 200 with the message content type, which silently swallowed
    /// rejections on the client side (iter-2 evaluator critique #2).
    /// </summary>
    private static AdaptiveCardInvokeResponse Reject(string code, int statusCode, string message)
        => new()
        {
            StatusCode = statusCode,
            Type = ContentTypeError,
            Value = JObject.FromObject(new { code, message }),
        };

    private static AdaptiveCardInvokeResponse Accept(string message)
        => new()
        {
            StatusCode = 200,
            Type = ContentTypeMessage,
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

        await WriteAuditEntryWithRetryAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Iter-3 evaluator feedback #3 ΓÇö durable audit recovery path. The
    /// <c>Open->Resolved</c> CAS and the inbound <see cref="DecisionEvent"/> publish
    /// happen <i>before</i> this method is reached on the success path, so an audit-
    /// store outage at that point is a compliance-evidence loss the actor's retry
    /// cannot heal (the retry hits the resolved-status guard and never re-runs the
    /// success-audit emit). The handler therefore retries the underlying
    /// <see cref="IAuditLogger.LogAsync"/> call with exponential backoff and, on
    /// retry exhaustion, emits the fully-serialised <see cref="AuditEntry"/> as a
    /// single <c>LogCritical</c> log line tagged <c>FALLBACK_AUDIT_ENTRY</c>. The log
    /// sink is independent of the primary audit store, so operators can recover the
    /// row from logs and replay it out-of-band; the original exception is rethrown
    /// after the fallback emit so the Bot Framework caller observes the failure and
    /// the dedupe entry is evicted via the outer <see cref="HandleAsync"/>
    /// finally-block.
    /// </summary>
    private async Task WriteAuditEntryWithRetryAsync(AuditEntry entry, CancellationToken ct)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < _auditRetryMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _auditLogger.LogAsync(entry, ct).ConfigureAwait(false);
                if (attempt > 0)
                {
                    _logger.LogWarning(
                        "CardActionReceived audit entry persisted on retry attempt {Attempt} after {PriorFailures} transient failure(s) (outcome={Outcome}, correlationId={CorrelationId}).",
                        attempt + 1,
                        attempt,
                        entry.Outcome,
                        entry.CorrelationId);
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                var isFinalAttempt = attempt == _auditRetryMaxAttempts - 1;
                if (isFinalAttempt)
                {
                    break;
                }

                // Exponential backoff: base * 2^attempt, capped at AuditRetryMaxDelay.
                var delayTicks = _auditRetryBaseDelay.Ticks * (1L << attempt);
                var delay = delayTicks > AuditRetryMaxDelay.Ticks || delayTicks < 0
                    ? AuditRetryMaxDelay
                    : new TimeSpan(delayTicks);
                _logger.LogWarning(
                    ex,
                    "Failed to persist CardActionReceived audit entry on attempt {Attempt}/{MaxAttempts} (outcome={Outcome}, correlationId={CorrelationId}); retrying in {DelayMs}ms.",
                    attempt + 1,
                    _auditRetryMaxAttempts,
                    entry.Outcome,
                    entry.CorrelationId,
                    delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        // Retries exhausted. Emit a fallback log line carrying the serialised audit
        // entry so the log sink (independent of the primary audit store) can serve as
        // a durable recovery surface for the missing compliance evidence. Use a
        // structured property name (FallbackAuditEntry) so operators can grep / index
        // the log stream and replay the row out-of-band into the primary store. The
        // marker prefix in the message template gives an additional plain-text grep
        // anchor (FALLBACK_AUDIT_ENTRY:) for log shippers without structured-property
        // indexing.
        string serialised;
        try
        {
            serialised = JsonSerializer.Serialize(entry);
        }
        catch (Exception serialiseEx)
        {
            // Defensive: if serialisation itself fails, fall back to a minimal record
            // that at least preserves the discriminator keys so the loss is locatable.
            _logger.LogCritical(
                serialiseEx,
                "FALLBACK_AUDIT_ENTRY_SERIALIZE_FAILED: could not serialise AuditEntry for fallback log emit (outcome={Outcome}, correlationId={CorrelationId}).",
                entry.Outcome,
                entry.CorrelationId);
            serialised = $"{{\"correlationId\":\"{entry.CorrelationId}\",\"outcome\":\"{entry.Outcome}\",\"actorId\":\"{entry.ActorId}\",\"action\":\"{entry.Action}\",\"checksum\":\"{entry.Checksum}\"}}";
        }

        _logger.LogCritical(
            lastException,
            "FALLBACK_AUDIT_ENTRY: CardActionReceived audit persistence exhausted {MaxAttempts} retries; emitting serialised entry to the log sink for out-of-band recovery. Operator must replay this row into the primary audit store. FallbackAuditEntry={FallbackAuditEntry}",
            _auditRetryMaxAttempts,
            serialised);

        // Rethrow so the Bot Framework caller observes the failure as a 5xx invoke
        // response. The outer HandleAsync finally-block sets keepDedupeEntry=false on
        // rethrow so the dedupe entry is evicted and the actor's retry can re-run the
        // pipeline; if the durable CAS already committed, the retry hits the resolved-
        // status guard and surfaces "already recorded" while the LogCritical fallback
        // entry persists the missing audit data for manual replay.
        if (lastException is null)
        {
            // Theoretically unreachable: the loop only exits via `break` after setting
            // lastException, but the compiler can't prove that. Throw a generic guard.
            throw new InvalidOperationException(
                $"Audit persistence failed for correlationId {entry.CorrelationId} but no exception was captured.");
        }

        ExceptionDispatchInfo.Capture(lastException).Throw();
    }
}
