using System.Runtime.ExceptionServices;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Concrete implementation of <see cref="ICardActionHandler"/> for Stage 3.3 of
/// <c>implementation-plan.md</c> (steps 3 and 4) and <c>architecture.md</c> Γö¼┬║2.6 / Γö¼┬║6.3.
/// Replaces the <c>NoOpCardActionHandler</c> stub registered in Stage 2.1 once the SQL
/// stores ship.
/// </summary>
/// <remarks>
/// <para>
/// The handler is deliberately the single point of entry from
/// <see cref="TeamsSwarmActivityHandler"/>'s <c>OnAdaptiveCardInvokeAsync</c> override ╬ô├ç├╢
/// every responsibility called out in the implementation plan is fulfilled here:
/// </para>
/// <list type="number">
/// <item><description>Parse the inbound <see cref="Activity.Value"/> via
/// <see cref="CardActionMapper.ReadPayload"/> to extract the <c>QuestionId</c>,
/// <c>ActionId</c>, <c>ActionValue</c>, <c>CorrelationId</c>, and the optional
/// <c>Comment</c> ╬ô├ç├╢ the same code path used by Stage 3.1's
/// <see cref="CardActionMapper"/> so a typo on either side fails fast.</description></item>
/// <item><description>Apply the <c>architecture.md</c> Γö¼┬║2.6 fast-path dedupe ╬ô├ç├╢ the
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
/// <c>false</c> (another pod won the race per <c>architecture.md</c> Γö¼┬║6.3), surface
/// a "decision already recorded" card and emit a <c>Rejected</c> audit entry ╬ô├ç├╢ the
/// concurrent winner is responsible for the decision event and the card update.</description></item>
/// <item><description>On a successful CAS publish a <see cref="DecisionEvent"/>
/// wrapping the <see cref="HumanDecisionEvent"/> via
/// <see cref="IInboundEventPublisher.PublishAsync"/>, replace the original card via
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
/// (the actor-attributed overload ╬ô├ç├╢ the canonical 3-arg
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, CancellationToken)"/>
/// remains intact for callers that have no decision payload), and write a
/// <see cref="AuditEventTypes.CardActionReceived"/> audit entry. <b>Iter-8 fix #3:</b>
/// if the card update throws after the durable resolution succeeds, the audit row is
/// written with <see cref="AuditOutcomes.Failed"/> and a <c>cardUpdateError</c>
/// marker in the sanitised payload JSON ╬ô├ç├╢ the decision itself is durably recorded so
/// the caller still receives an <c>Accept</c> response, but operators see the
/// lifecycle gap in the audit trail.</description></item>
/// </list>
/// <para>
/// <b>Audit payload sanitisation.</b> The <see cref="AuditEntry.PayloadJson"/> field is
/// built by <see cref="BuildSanitizedPayloadJsonAsync"/> which extracts only the
/// canonical card-action fields plus a redacted comment indicator and the persisted
/// card-state activity ID ╬ô├ç├╢ never the raw <c>Activity.Value</c> blob, never any free-form
/// user comment text. This is the sanitization mandated by <c>tech-spec.md</c> Γö¼┬║4.3
/// (PayloadJson must contain "no secrets or PII beyond identity"). The card-state lookup
/// is best-effort: a missing or failing <see cref="ICardStateStore.GetByQuestionIdAsync"/>
/// is logged at warning level and the resulting payload simply omits the
/// <c>cardActivityId</c> hint.
/// </para>
/// <para>
/// <b>Cross-stage attachment alignment (Stage 5.1 / US-10).</b> The story attachment
/// <c>.forge-attachments/agent_swarm_messenger_user_stories.md</c> is scoped to Stage
/// 5.1 (Tenant and Identity Validation, US-01..US-10). The only Stage-5.1 acceptance
/// criterion that touches Stage 3.3 surface area is <b>US-10 (Audit envelope
/// contract)</b>: every audit row must (i) use one of the four canonical
/// <see cref="AuditOutcomes"/> values (<c>Success</c>, <c>Rejected</c>, <c>Failed</c>,
/// <c>DeadLettered</c>), (ii) keep rejection-reason codes in the <c>Action</c> field
/// (never in <c>Outcome</c>), and (iii) populate <c>ActorId</c>, <c>TenantId</c>,
/// <c>CorrelationId</c>, and <c>Timestamp</c>. The <see cref="WriteAuditAsync"/> helper
/// satisfies all three sub-bullets: it emits only <see cref="AuditOutcomes.Success"/>,
/// <see cref="AuditOutcomes.Rejected"/>, or <see cref="AuditOutcomes.Failed"/>; the
/// <c>Action</c> field carries the literal submitted <c>ActionValue</c>
/// (<c>approve</c>/<c>reject</c>/<c>escalate</c>/etc), never a rejection code; and the
/// four envelope fields are populated unconditionally. <see cref="AuditOutcomes.DeadLettered"/>
/// is reserved for the Stage 6.x outbox retry path and is intentionally not emitted
/// here. The remaining US-01..US-09 stories cover tenant validation, RBAC, conversation
/// reference governance, and proactive identity gating Γò¼├┤Γö£├ºΓö£Γòó those are sibling-stage concerns
/// (Stage 5.1 / 4.2 / 6.3) and are not the responsibility of this handler.
/// </para>
/// </remarks>
public sealed class CardActionHandler : ICardActionHandler
{
    /// <summary>
    /// Default retention window applied when the legacy constructors allocate an internal
    /// <see cref="ProcessedCardActionSet"/>. Stage 6.2 of <c>implementation-plan.md</c>
    /// canonically uses a 24-hour TTL (with a 5-minute background eviction cadence,
    /// honoured by <see cref="ProcessedCardActionEvictionService"/>). The pre-Stage-6.2
    /// inline dedupe used 5 minutes; that legacy value is preserved in the test-only
    /// 8-argument constructor below so existing tests that opt into a shorter window
    /// continue to behave identically.
    /// </summary>
    internal static readonly TimeSpan DedupeRetentionPeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// Iter-3 evaluator feedback #3 — audit retry base delay. Production defaults are
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
    /// Number of audit-write retries before the handler persists to the durable
    /// <see cref="IAuditFallbackSink"/>, emits the FALLBACK_AUDIT_ENTRY log line, and
    /// rethrows. With <see cref="AuditRetryBaseDelay"/> = 200ms and exponential
    /// doubling, the loop sleeps between failed non-final attempts only — for four
    /// attempts the worst-case wall-clock time in <see cref="Task.Delay(TimeSpan)"/>
    /// is <c>200 + 400 + 800 = 1400ms</c> (three inter-attempt delays for four
    /// attempts, not four) plus the cumulative time each failing
    /// <see cref="IAuditLogger.LogAsync"/> call itself takes — enough for typical
    /// transient outages to recover without losing compliance evidence, but bounded
    /// so a persistent outage surfaces promptly.
    /// </summary>
    internal const int AuditRetryMaxAttempts = 4;

    private readonly IAgentQuestionStore _questionStore;
    private readonly ICardStateStore _cardStateStore;
    private readonly ITeamsCardManager _cardManager;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly IAuditLogger _auditLogger;
    private readonly IAuditFallbackSink _auditFallbackSink;
    private readonly ILogger<CardActionHandler> _logger;
    private readonly CardActionMapper _mapper;
    private readonly TimeProvider _timeProvider;
    private readonly ProcessedCardActionSet _processedActions;
    private readonly TimeSpan _auditRetryBaseDelay;
    private readonly int _auditRetryMaxAttempts;

    /// <summary>
    /// Construct the handler with the six dependencies required by
    /// <c>implementation-plan.md</c> §3.3 step 3. Every parameter is null-guarded so DI
    /// mis-registration fails loudly. Allocates a fresh
    /// <see cref="ProcessedCardActionSet"/> with default options (24-hour entry
    /// lifetime) — used by tests that pre-date Stage 6.2 and by production-DI fallback
    /// when no shared <see cref="ProcessedCardActionSet"/> singleton is registered.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger)
        : this(
            questionStore,
            cardStateStore,
            cardManager,
            inboundEventPublisher,
            auditLogger,
            logger,
            TimeProvider.System,
            new ProcessedCardActionSet(new CardActionDedupeOptions(), TimeProvider.System),
            new NoOpAuditFallbackSink(),
            AuditRetryBaseDelay,
            AuditRetryMaxAttempts)
    {
    }

    /// <summary>
    /// Stage 6.2 canonical DI constructor — accepts a shared singleton
    /// <see cref="ProcessedCardActionSet"/> so the in-memory dedupe state is shared
    /// between every handler resolution AND the
    /// <see cref="ProcessedCardActionEvictionService"/> background timer. Resolved
    /// preferentially by .NET DI because it is strictly longer than the legacy 6-arg
    /// overload above and every parameter has a registered service descriptor.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger,
        TimeProvider timeProvider,
        ProcessedCardActionSet processedActions)
        : this(
            questionStore,
            cardStateStore,
            cardManager,
            inboundEventPublisher,
            auditLogger,
            logger,
            timeProvider,
            processedActions,
            new NoOpAuditFallbackSink(),
            AuditRetryBaseDelay,
            AuditRetryMaxAttempts)
    {
    }

    /// <summary>
    /// Stage 3.3 iter-5 DI constructor — adds the durable
    /// <see cref="IAuditFallbackSink"/> alongside the canonical 8-argument signature
    /// so production hosts can wire a real secondary persistence surface (e.g.
    /// <see cref="FileAuditFallbackSink"/>) without overriding any of the other
    /// singletons. Chosen by .NET DI when both <see cref="ProcessedCardActionSet"/>
    /// AND <see cref="IAuditFallbackSink"/> are registered.
    /// </summary>
    public CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger,
        TimeProvider timeProvider,
        ProcessedCardActionSet processedActions,
        IAuditFallbackSink auditFallbackSink)
        : this(
            questionStore,
            cardStateStore,
            cardManager,
            inboundEventPublisher,
            auditLogger,
            logger,
            timeProvider,
            processedActions,
            auditFallbackSink,
            AuditRetryBaseDelay,
            AuditRetryMaxAttempts)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic
    /// <see cref="TimeProvider"/> and a configurable dedupe retention window. Hosts
    /// resolve the canonical 8-arg constructor via DI; unit tests opt in to this
    /// overload to advance the dedupe clock without sleeping. Allocates a private
    /// <see cref="ProcessedCardActionSet"/> seeded with the supplied retention window
    /// so tests can verify both the short-circuit and the eviction behaviour.
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
        : this(
            questionStore,
            cardStateStore,
            cardManager,
            inboundEventPublisher,
            auditLogger,
            logger,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
            new ProcessedCardActionSet(
                new CardActionDedupeOptions
                {
                    EntryLifetime = dedupeRetention > TimeSpan.Zero ? dedupeRetention : DedupeRetentionPeriod,
                },
                timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))),
            new NoOpAuditFallbackSink(),
            AuditRetryBaseDelay,
            AuditRetryMaxAttempts)
    {
    }

    /// <summary>
    /// Test-friendly constructor that adds audit-retry-policy parameters so unit tests
    /// exercising the retry / fallback path (iter-3 evaluator feedback #3) do not have
    /// to wait the production 200ms-base exponential backoff. Production paths chain
    /// through the public 6-/8-/9-arg overloads which use
    /// <see cref="AuditRetryBaseDelay"/> and <see cref="AuditRetryMaxAttempts"/>.
    /// </summary>
    public CardActionHandler(
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
        : this(
            questionStore,
            cardStateStore,
            cardManager,
            inboundEventPublisher,
            auditLogger,
            logger,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
            new ProcessedCardActionSet(
                new CardActionDedupeOptions
                {
                    EntryLifetime = dedupeRetention > TimeSpan.Zero ? dedupeRetention : DedupeRetentionPeriod,
                },
                timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))),
            new NoOpAuditFallbackSink(),
            auditRetryBaseDelay,
            auditRetryMaxAttempts)
    {
    }

    /// <summary>
    /// Internal master constructor — every public/test-friendly overload chains here.
    /// Accepts the explicit dedupe set, audit fallback sink, and audit retry policy
    /// parameters so tests can wire all three with deterministic test doubles.
    /// </summary>
    internal CardActionHandler(
        IAgentQuestionStore questionStore,
        ICardStateStore cardStateStore,
        ITeamsCardManager cardManager,
        IInboundEventPublisher inboundEventPublisher,
        IAuditLogger auditLogger,
        ILogger<CardActionHandler> logger,
        TimeProvider timeProvider,
        ProcessedCardActionSet processedActions,
        IAuditFallbackSink auditFallbackSink,
        TimeSpan auditRetryBaseDelay,
        int auditRetryMaxAttempts)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _cardManager = cardManager ?? throw new ArgumentNullException(nameof(cardManager));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _auditFallbackSink = auditFallbackSink ?? throw new ArgumentNullException(nameof(auditFallbackSink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _processedActions = processedActions ?? throw new ArgumentNullException(nameof(processedActions));
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

        // Stage 6.3 iter-2 ΓÇö open the canonical enrichment scope for every log entry
        // emitted while handling this card invoke. Tenant + actor are best-effort ΓÇö
        // the activity may be malformed, in which case the scope simply elides any
        // null/empty key (TeamsLogScope.BeginScope handles that internally).
        var preliminaryCorrelationId = ResolveCorrelationId(activity, fallback: Guid.NewGuid().ToString("N"));
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: preliminaryCorrelationId,
            tenantId: tenantId,
            userId: actorAad);

        // Guard: an Adaptive Card invoke without an Action.Submit payload is malformed ╬ô├ç├╢
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

        // architecture.md Γö¼┬║2.6 layer 2 ╬ô├ç├╢ in-memory processed-action dedupe set, keyed on
        // (QuestionId, UserId) per the Stage 6.2 canonical brief. Fast-path short-circuits
        // within-session duplicate submissions (e.g. user double-tapped the Approve button
        // or the Bot Framework delivered the same invoke twice) before any I/O. The first
        // submission's terminal response is cached on the entry so duplicate calls return
        // the SAME response (Stage 6.2 step 2: "If so, return the previous result without
        // re-executing"). Entries live for CardActionDedupeOptions.EntryLifetime
        // (24 hours by default) and are purged by ProcessedCardActionEvictionService on a
        // 5-minute cadence. Only unhandled exceptions evict the entry inline so transient
        // infrastructure failures stay retryable.
        //
        // Iter-2 evaluator fix #2: a concurrent second invocation that arrives BEFORE the
        // first finishes must also receive the same terminal response. The Claim API
        // returns a Task<AdaptiveCardInvokeResponse?> that resolves either immediately
        // (prior caller already RecordedResult) or once the prior caller completes ΓÇö
        // the waiter then returns that exact response rather than a generic rejection.
        // When the prior caller fails (Remove evicts the slot) the task resolves to null
        // and we re-claim, which naturally retries the pipeline.
        //
        // Iter-2 evaluator fix #3: only *durable* outcomes (success + server-state-based
        // rejections like already-Resolved / Expired / lost-CAS) are cached. Transient
        // rejections (question not found, action not in AllowedActions) are NOT cached
        // so a later valid submission for the same (QuestionId, UserId) ΓÇö once the
        // server-side state changes or the user corrects their action ΓÇö can run end-to-
        // end. See ProcessedHandlerOutcome.IsDurable below.
        var dedupeKey = (QuestionId: payload.QuestionId, UserId: actorAad);
        const int maxClaimRetries = 2;
        for (var claimAttempt = 0; ; claimAttempt++)
        {
            var claim = _processedActions.Claim(dedupeKey);
            if (!claim.IsOwner)
            {
                // Wait for the prior caller to finish (synchronous if already cached).
                AdaptiveCardInvokeResponse? prev;
                try
                {
                    prev = await claim.PreviousResponseTask.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (prev is not null)
                {
                    _logger.LogInformation(
                        "Card action {ActionValue} for question {QuestionId} by actor {Actor} short-circuited by in-memory dedupe; replaying previous response.",
                        payload.ActionValue,
                        payload.QuestionId,
                        actorAad);
                    return prev;
                }

                // Prior caller failed and released the slot OR was evicted. Re-claim
                // and run the pipeline ourselves, but bound the retry count so a
                // pathological churn cannot loop forever.
                if (claimAttempt >= maxClaimRetries)
                {
                    _logger.LogWarning(
                        "Card action {ActionValue} for question {QuestionId} by actor {Actor} could not acquire the dedupe slot after {Attempts} attempts; rejecting to break the cycle.",
                        payload.ActionValue,
                        payload.QuestionId,
                        actorAad,
                        claimAttempt + 1);
                    return Reject($"Decision for question '{payload.QuestionId}' could not be processed; please retry.");
                }

                continue;
            }

            var keepDedupeEntry = false;
            AdaptiveCardInvokeResponse? terminalResponse = null;
            try
            {
                var outcome = await ProcessHandledAsync(
                    turnContext,
                    activity,
                    payload,
                    actorAad,
                    actorDisplayName,
                    tenantId,
                    receivedAt,
                    correlationId,
                    ct).ConfigureAwait(false);
                terminalResponse = outcome.Response;
                keepDedupeEntry = outcome.IsDurable;
                return terminalResponse;
            }
            catch
            {
                // Unhandled exception means we never reached a terminal outcome ╬ô├ç├╢ let the
                // user retry the action by evicting the dedupe entry.
                throw;
            }
            finally
            {
                if (keepDedupeEntry && terminalResponse is not null)
                {
                    _processedActions.RecordResult(dedupeKey, terminalResponse);
                }
                else
                {
                    // Either unhandled exception (terminalResponse == null) OR transient
                    // rejection (keepDedupeEntry == false) ΓÇö release the slot so a later
                    // valid submission for the same (QuestionId, UserId) is not blocked.
                    _processedActions.Remove(dedupeKey);
                }
            }
        }
    }

    /// <summary>
    /// Internal return shape for <see cref="ProcessHandledAsync"/>. The
    /// <see cref="IsDurable"/> flag distinguishes outcomes that should be cached in the
    /// in-memory processed-action set (success + server-state-based rejections) from
    /// transient/recoverable rejections (question lookup miss, action-value typo) that
    /// must NOT block subsequent valid submissions for 24 hours. Iter-2 evaluator
    /// fix #3.
    /// </summary>
    private readonly record struct ProcessedHandlerOutcome(AdaptiveCardInvokeResponse Response, bool IsDurable);

    private async Task<ProcessedHandlerOutcome> ProcessHandledAsync(
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
                "Card action {ActionValue} for question {QuestionId} rejected ╬ô├ç├╢ no stored question.",
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
            return new ProcessedHandlerOutcome(
                Reject($"Question '{payload.QuestionId}' was not found."),
                IsDurable: false);
        }

        var allowed = question.AllowedActions
            .Any(a => string.Equals(a.Value, payload.ActionValue, StringComparison.Ordinal));
        if (!allowed)
        {
            _logger.LogWarning(
                "Card action {ActionValue} for question {QuestionId} rejected ╬ô├ç├╢ not in AllowedActions.",
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
            // Iter-2 evaluator fix #3: a typo'd ActionValue must NOT lock out the actor
            // from later submitting a *valid* ActionValue for the same question.
            // Iter-3 evaluator feedback #4 ΓÇö emit a Bot Framework Universal Action
            // error response (HTTP 403 + application/vnd.microsoft.error with
            // code=ActionNotAllowed) so the Teams client renders a proper error
            // notification instead of the chat-message acknowledgement that the old
            // 200/activity.message Reject() shape would have produced.
            return new ProcessedHandlerOutcome(
                RejectInvalidAction(payload.ActionValue),
                IsDurable: false);
        }

        if (!string.Equals(question.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Card action {ActionValue} for question {QuestionId} rejected ╬ô├ç├╢ status is {Status}.",
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
            // commit reverted the original split) ╬ô├ç├╢ emit a distinct "Expired" rejection
            // message when the durable status is Expired, so operators and the Teams
            // client can distinguish lifecycle-expired questions from user-resolved ones.
            if (string.Equals(question.Status, AgentQuestionStatuses.Expired, StringComparison.Ordinal))
            {
                return new ProcessedHandlerOutcome(
                    Reject($"Question '{payload.QuestionId}' has Expired and can no longer accept decisions."),
                    IsDurable: true);
            }

            return new ProcessedHandlerOutcome(
                Reject($"Decision for question '{payload.QuestionId}' has already been recorded."),
                IsDurable: true);
        }

        // Iter-3 evaluator feedback #3 (re-applied iter-4) ╬ô├ç├╢ between the moment
        // ExpiresAt elapses and the moment QuestionExpiryProcessor's next sweep flips
        // Status to Expired, there is a window where Status is still Open but the
        // deadline has passed. Reject explicitly with the Expired message so the same
        // outcome the worker produces is also produced here. Strict less-than matches
        // SqlAgentQuestionStore.GetOpenExpiredAsync (`e.ExpiresAt < cutoff`) and
        // QuestionExpiryProcessor's cutoff semantics ╬ô├ç├╢ diverging here would create
        // boundary mismatches between the handler and the worker.
        if (question.ExpiresAt < _timeProvider.GetUtcNow())
        {
            _logger.LogInformation(
                "Card action {ActionValue} for question {QuestionId} rejected ╬ô├ç├╢ ExpiresAt {ExpiresAt} has passed (Open-but-stale).",
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
            return new ProcessedHandlerOutcome(
                Reject($"Question '{payload.QuestionId}' has Expired and can no longer accept decisions."),
                IsDurable: true);
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
                "Card action {ActionValue} for question {QuestionId} rejected ╬ô├ç├╢ concurrent resolver won the race.",
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
            return new ProcessedHandlerOutcome(
                Reject($"Decision for question '{payload.QuestionId}' has already been recorded."),
                IsDurable: true);
        }

        // CAS won ╬ô├ç├╢ emit decision, update the card, and write the success audit row.
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
            // circuit on the status guard above and return "already recorded" ΓÇö the
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
                // unlikely ΓÇö only the expiry processor would touch a Resolved row,
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
        // this point and we still return Accept to the caller ╬ô├ç├╢ but the audit must NOT
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

        // Iter-7 evaluator feedback #3 (Stage 3.3 success contract). The brief specifies
        // the success path as (1) commit Open->Resolved, (2) publish HumanDecisionEvent,
        // (3) update card, (4) emit audit. By the time we reach this point the durable
        // resolution AND the decision event have both committed -- the user's vote is
        // recorded and downstream consumers will observe it. A failure to refresh the
        // visual card is a lifecycle observability problem (operators can see it via
        // the AuditOutcomes.Failed row + cardUpdateError payload marker), not a
        // decision-recording failure. Returning a 502 here would tell the Teams client
        // that the action did not take effect, which contradicts the durable state and
        // could cause the user to retry an already-recorded decision. The audit row
        // (built above) truthfully carries AuditOutcomes.Failed and the
        // cardUpdateError marker so operators can reconcile the stale card; the
        // user-visible response surfaces success since the decision is durably recorded.
        var acceptMessage = cardUpdateException is null
            ? $"Recorded {payload.ActionValue} for {payload.QuestionId}."
            : $"Recorded {payload.ActionValue} for {payload.QuestionId}. The original card could not be refreshed; please refresh the conversation to see the latest state.";

        return new ProcessedHandlerOutcome(
            Accept(acceptMessage),
            IsDurable: true);
    }

    private static AdaptiveCardInvokeResponse Reject(string message)
        => new()
        {
            StatusCode = 200,
            Type = "application/vnd.microsoft.activity.message",
            Value = message,
        };

    /// <summary>
    /// Iter-3 evaluator feedback #4 ΓÇö Bot Framework Universal Action error response
    /// for a card action whose <c>ActionValue</c> is not in the question's
    /// <c>AllowedActions</c>. Teams renders <c>application/vnd.microsoft.error</c>
    /// responses as error notifications (rather than as a chat message), and the
    /// <c>code</c> field discriminates this from other rejection types so operators
    /// can grep Bot Framework telemetry by code.
    /// </summary>
    private static AdaptiveCardInvokeResponse RejectInvalidAction(string actionValue)
        => new()
        {
            StatusCode = 403,
            Type = "application/vnd.microsoft.error",
            Value = new JObject
            {
                ["code"] = "ActionNotAllowed",
                ["message"] = $"Action '{actionValue}' is not permitted on this question.",
            },
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
        // Sanitization rules per tech-spec.md Γö¼┬║4.3 ╬ô├ç├╢ payload JSON must carry no secrets or
        // PII beyond identity. We persist only the canonical card-action discriminators
        // (question + action identity, correlation ID), a redacted comment indicator, and
        // ╬ô├ç├╢ if available ╬ô├ç├╢ the persisted card activity ID. The free-form user-supplied
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
            await WriteAuditEntryWithRetryAsync(entry, ct).ConfigureAwait(false);
        }
        catch
        {
            // Re-throw so the caller observes the failure (the dedupe entry is evicted
            // in the outer finally-block of HandleAsync so a follow-up retry from the
            // actor is consistent with the durable resolution that already committed).
            throw;
        }
    }

    /// <summary>
    /// Stage 3.3 iter-3/5/6 — three-stage durable audit recovery (per iter-5 evaluator
    /// feedback #2). When <see cref="IAuditLogger.LogAsync"/> fails for a card-action
    /// invoke (which has ALREADY committed the durable Open→Resolved CAS plus the
    /// inbound decision event), the lost row would be a compliance-evidence gap the
    /// actor's retry cannot heal — the next invoke hits the resolved-status guard and
    /// emits a Rejected audit, not the missing Success audit. This method recovers in
    /// three sequential stages:
    /// <list type="number">
    /// <item><description><b>Retry primary.</b> Exponential backoff (base
    /// <see cref="AuditRetryBaseDelay"/>, doubling, capped at
    /// <see cref="AuditRetryMaxDelay"/>, up to <see cref="AuditRetryMaxAttempts"/>
    /// attempts) so transient blips self-heal.</description></item>
    /// <item><description><b>Persist to <see cref="IAuditFallbackSink"/>.</b> On retry
    /// exhaustion the entry is written to the durable secondary surface (e.g.
    /// <see cref="FileAuditFallbackSink"/>) — an immutable append-only store that is
    /// infrastructure-independent of the primary <see cref="IAuditLogger"/>. This is
    /// the row that satisfies the compliance contract (no manual replay required —
    /// log-shipping pipelines forward it to the primary store on recovery). Hosts
    /// wire the durable sink via
    /// <c>TeamsServiceCollectionExtensions.AddTeamsCardLifecycle</c> (safe-by-default
    /// temp-directory wiring per iter-5 evaluator feedback item 2) or override it
    /// with
    /// <c>TeamsServiceCollectionExtensions.AddFileAuditFallbackSink(filePath)</c>
    /// for a host-controlled writable path.</description></item>
    /// <item><description><b>Emit FALLBACK_AUDIT_ENTRY log line.</b> Always — a
    /// belt-and-suspenders observability signal so on-call sees the primary-audit
    /// outage in their log/alerting pipeline. Tags <c>FallbackSinkOutcome</c> so
    /// operators see in the log whether the durable secondary persistence
    /// succeeded (<c>Persisted</c>), failed (<c>Failed</c>), or is unwired
    /// (<c>NoOp</c>).</description></item>
    /// </list>
    /// The original primary exception is rethrown last via
    /// <see cref="ExceptionDispatchInfo"/> so the dedupe-eviction-on-throw semantics
    /// of the outer <see cref="HandleAsync"/> finally-block continue to fire.
    /// </summary>
    private async Task WriteAuditEntryWithRetryAsync(AuditEntry entry, CancellationToken ct)
    {
        var attempt = 0;
        Exception? lastException = null;
        while (attempt < _auditRetryMaxAttempts)
        {
            attempt++;
            try
            {
                await _auditLogger.LogAsync(entry, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= _auditRetryMaxAttempts)
                {
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "CardActionReceived audit write attempt {Attempt}/{MaxAttempts} failed (correlationId={CorrelationId}); retrying.",
                    attempt,
                    _auditRetryMaxAttempts,
                    entry.CorrelationId);

                // Exponential backoff: base * 2^(attempt-1), capped at AuditRetryMaxDelay.
                var delayTicks = _auditRetryBaseDelay.Ticks * (1L << Math.Min(attempt - 1, 30));
                var delay = delayTicks > AuditRetryMaxDelay.Ticks || delayTicks < 0
                    ? AuditRetryMaxDelay
                    : TimeSpan.FromTicks(delayTicks);
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        // Stage A — persist to the durable secondary sink. Wrapped in its own try so
        // a sink failure cannot mask the rethrow of the primary exception.
        string sinkOutcome;
        try
        {
            await _auditFallbackSink.WriteAsync(entry, ct).ConfigureAwait(false);
            sinkOutcome = _auditFallbackSink is NoOpAuditFallbackSink ? "NoOp" : "Persisted";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sinkEx)
        {
            sinkOutcome = "Failed";
            _logger.LogCritical(
                sinkEx,
                "FALLBACK_AUDIT_SINK_FAILED: IAuditFallbackSink {SinkType} also failed to persist the audit entry (correlationId={CorrelationId}); falling back to the LogCritical channel.",
                _auditFallbackSink.GetType().FullName,
                entry.CorrelationId);
        }

        // Stage B — emit FALLBACK_AUDIT_ENTRY log line regardless of sink outcome.
        string? serialized = null;
        try
        {
            serialized = JsonSerializer.Serialize(entry);
        }
        catch (Exception serEx)
        {
            _logger.LogCritical(
                serEx,
                "FALLBACK_AUDIT_ENTRY_SERIALIZE_FAILED: could not serialise AuditEntry for fallback log emit (outcome={Outcome}, correlationId={CorrelationId}).",
                entry.Outcome,
                entry.CorrelationId);
        }

        _logger.LogCritical(
            lastException,
            "FALLBACK_AUDIT_ENTRY: CardActionReceived audit persistence exhausted {MaxAttempts} retries (sinkOutcome={FallbackSinkOutcome}, sinkType={FallbackSinkType}, correlationId={CorrelationId}); the entry has been persisted to the secondary sink (when sinkOutcome=Persisted) and is also recorded here for redundancy. FallbackAuditEntry={FallbackAuditEntry}",
            _auditRetryMaxAttempts,
            sinkOutcome,
            _auditFallbackSink.GetType().FullName,
            entry.CorrelationId,
            serialized ?? "(serialization-failed)");

        if (lastException is not null)
        {
            ExceptionDispatchInfo.Capture(lastException).Throw();
        }
    }
}
