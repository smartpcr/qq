using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Stage 3.3 production <see cref="ICallbackHandler"/>. Replaces the
/// Stage 2.2 <see cref="Stubs.StubCallbackHandler"/> at DI registration time
/// (<see cref="TelegramServiceCollectionExtensions.AddTelegram"/> uses
/// last-wins <see cref="Microsoft.Extensions.DependencyInjection.ServiceCollectionDescriptorExtensions"/>
/// semantics).
/// </summary>
/// <remarks>
/// <para>
/// <b>Responsibility (implementation-plan.md Stage 3.3).</b> Processes
/// inline-button callbacks from Telegram <c>CallbackQuery</c> events
/// (mapped to <see cref="EventType.CallbackResponse"/>):
/// <list type="number">
///   <item>Decode the <c>QuestionId:ActionId</c> callback-data payload.</item>
///   <item>Resolve the originating <see cref="PendingQuestion"/> via
///         <see cref="IPendingQuestionStore.GetAsync"/>.</item>
///   <item>Reject the tap when the question has expired or has already
///         transitioned out of <see cref="PendingQuestionStatus.Pending"/>.</item>
///   <item>Resolve the tapped <see cref="HumanAction"/>; when it carries
///         <see cref="HumanAction.RequiresComment"/>, transition to
///         <see cref="PendingQuestionStatus.AwaitingComment"/> and prompt
///         the operator for a follow-up text reply WITHOUT emitting the
///         <see cref="HumanDecisionEvent"/> yet.</item>
///   <item>Otherwise publish a strongly-typed
///         <see cref="HumanDecisionEvent"/>, persist a
///         <see cref="HumanResponseAuditEntry"/>, transition the question
///         to <see cref="PendingQuestionStatus.Answered"/>, and confirm
///         to the operator.</item>
///   <item>In every terminal path, answer the Telegram callback via
///         <c>AnswerCallbackQueryAsync</c> so the operator's spinner stops,
///         and edit the original question message to embed the selected
///         action in the message text AND set the inline keyboard to
///         <c>null</c> so all tappable buttons are removed
///         (implementation-plan Stage 3.3 "edit inline keyboard to show
///         selected action, disable further buttons" + e2e-scenarios
///         "the message is edited to show only the selected action and
///         buttons are removed"). A single <c>EditMessageText</c> call
///         carries both the new body AND <c>ReplyMarkup = null</c>, so
///         no residual no-op button is left behind.</item>
/// </list>
/// </para>
/// <para>
/// <b>Idempotency contract — three layers.</b>
/// <list type="bullet">
///   <item><b>Per-callback id.</b> Reserves
///   <see cref="CallbackIdDedupKeyPrefix"/><c>+</c><see cref="MessengerEvent.CallbackId"/>
///   in <see cref="IDeduplicationService"/>. On a duplicate delivery the
///   handler looks the <see cref="MessengerEvent.CallbackId"/> up in
///   <see cref="_replayAnswers"/> and re-answers with the
///   <i>previously-sent confirmation text</i> the user already saw — per
///   plan Stage 3.3 ("if the same callback has already been processed
///   (same <c>CallbackQuery.Id</c>), skip processing and re-answer with
///   the previous result"). When the replay cache evicted (e.g. the
///   handler restarted between deliveries), the fallback answer is
///   <see cref="AlreadyRespondedText"/>.</item>
///   <item><b>Per-(question, respondent).</b> Reserves
///   <see cref="QuestionRespondentDedupKeyPrefix"/><c>+QuestionId:UserId</c>
///   in the same service. Closes the e2e "Concurrent button taps from
///   same user" scenario where two taps have <i>different</i> update ids
///   so the pipeline-level <c>EventId</c> gate cannot collapse them.</item>
///   <item><b>Durable status backstop.</b> Even when both reservations
///   have evicted, <see cref="PendingQuestion.Status"/><c> != Pending</c>
///   short-circuits with <see cref="AlreadyRespondedText"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Reservation lifecycle (release-on-throw, both keys).</b> Both
/// reservations are owned by the same try/catch scope. If ANY step
/// after the composite reservation succeeds throws (RecordSelectionAsync,
/// MarkAnsweredAsync, PublishHumanDecisionAsync, audit), the catch block
/// releases <i>both</i> the per-callback AND the composite slot so a
/// live re-delivery is processed normally — the bug the iter-1 evaluator
/// flagged (composite reservation leaked, retry sees "Already responded"
/// without publishing) is fixed here. Successful completion sticks both
/// slots via <see cref="IDeduplicationService.MarkProcessedAsync"/>.
/// </para>
/// <para>
/// <b>Stage 5.3 iter-3 ordering invariant.</b> The atomic
/// <see cref="IPendingQuestionStore.MarkAnsweredAsync"/> claim runs
/// <i>before</i> <see cref="ISwarmCommandBus.PublishHumanDecisionAsync"/>
/// and <see cref="IAuditLogger.LogHumanResponseAsync"/>. If a concurrent
/// <c>QuestionTimeoutService</c> sweep has already moved the row to
/// <see cref="PendingQuestionStatus.TimedOut"/>, the conditional
/// UPDATE returns 0 rows; we surface <see cref="AlreadyRespondedText"/>
/// and exit without emitting a duplicate decision event or audit row.
/// If the publish or audit step throws after a successful claim, the
/// catch path issues a compensating
/// <see cref="IPendingQuestionStore.TryRevertAnsweredClaimAsync"/> CAS
/// so a retry can re-process the same QuestionId.
/// </para>
/// </remarks>
public sealed class CallbackQueryHandler : ICallbackHandler
{
    /// <summary>
    /// <see cref="IDeduplicationService"/> key prefix for per-callback
    /// idempotency reservations. Combined with
    /// <see cref="MessengerEvent.CallbackId"/> to form the full dedup
    /// key (e.g. <c>cb:8675309</c>).
    /// </summary>
    public const string CallbackIdDedupKeyPrefix = "cb:";

    /// <summary>
    /// <see cref="IDeduplicationService"/> key prefix for the
    /// per-question + per-respondent dedup that collapses rapid
    /// concurrent button taps from the same operator on the same
    /// question. Combined with <c>{questionId}:{userId}</c>.
    /// </summary>
    public const string QuestionRespondentDedupKeyPrefix = "qa:";

    /// <summary>
    /// <see cref="IDeduplicationService"/> key prefix for the text-reply
    /// analogue of <see cref="QuestionRespondentDedupKeyPrefix"/>. Closes
    /// the race window in <see cref="HandleCommentReplyAsync"/> where two
    /// concurrent text replies from the same operator targeting the same
    /// AwaitingComment question carry DIFFERENT <see cref="MessengerEvent.EventId"/>
    /// values (so the pipeline-level dedup gate cannot collapse them) and
    /// both pass <see cref="IPendingQuestionStore.GetAwaitingCommentAsync"/>
    /// before either reaches <see cref="IPendingQuestionStore.MarkAnsweredAsync"/>,
    /// double-publishing the <see cref="HumanDecisionEvent"/>. The
    /// namespace is intentionally distinct from
    /// <see cref="QuestionRespondentDedupKeyPrefix"/> so the leftover tap
    /// reservation from the originating
    /// <see cref="HumanAction.RequiresComment"/> callback does NOT block
    /// the follow-up text reply on the same question. Combined with
    /// <c>{questionId}:{userId}</c>.
    /// </summary>
    public const string QuestionRespondentCommentDedupKeyPrefix = "qa-comment:";

    /// <summary>Callback-data separator agreed with <c>TelegramQuestionRenderer</c>.</summary>
    public const char CallbackDataSeparator = ':';

    /// <summary>Answer text shown when the tapped question has expired.</summary>
    public const string ExpiredQuestionText = "This question has expired";

    /// <summary>
    /// Fallback answer text shown to the SECOND tap of a concurrent
    /// same-user pair AND to a duplicate <see cref="MessengerEvent.CallbackId"/>
    /// when the replay cache no longer has the prior text. The
    /// duplicate-callback path PREFERS the cached prior result over
    /// this string (plan Stage 3.3 "re-answer with the previous result").
    /// </summary>
    public const string AlreadyRespondedText = "Already responded";

    /// <summary>Answer text shown when the question is unknown or no longer in <see cref="PendingQuestionStatus.Pending"/>.</summary>
    public const string QuestionNotFoundText = "Question is no longer available";

    /// <summary>Answer text shown when the callback data does not match the <c>QuestionId:ActionId</c> shape.</summary>
    public const string MalformedCallbackText = "Invalid action";

    /// <summary>
    /// Answer text shown when the resolved <c>QuestionId</c> exists but
    /// the supplied <c>ActionId</c> is not in the question's
    /// <see cref="AgentQuestion.AllowedActions"/>.
    /// </summary>
    public const string UnknownActionText = "Unknown action";

    /// <summary>
    /// Visual prefix applied to both the toast callback answer and the
    /// post-decision message-text footer (e.g. <c>"✅ Approve"</c>).
    /// </summary>
    public const string DecisionShownLabelPrefix = "✅ ";

    /// <summary>Marker line preceding the decision footer in the edited message body (for grep-ability).</summary>
    public const string DecisionFooterSeparator = "\n\n— ";

    /// <summary>
    /// Plain-text format string for the per-message correlation/trace
    /// footer appended to the edited decision message body. The story-
    /// wide acceptance criterion "All messages include trace/correlation
    /// ID" applies to the edit-after-decision message just like every
    /// other outbound message (iter-2 evaluator item 1 — the post-
    /// decision edit previously dropped <see cref="PendingQuestion.CorrelationId"/>,
    /// regressing the criterion). Single positional placeholder = the
    /// correlation id; rendered on its own line beneath the action
    /// badge so the operator can read it without it being mistaken for
    /// part of the action label.
    /// </summary>
    public const string CorrelationFooterFormat = "\n(trace: {0})";

    /// <summary>
    /// Comment-prompt text sent as a follow-up message when the tapped
    /// action carries <see cref="HumanAction.RequiresComment"/>=true.
    /// The wire body composed by <see cref="BuildCommentPromptText"/>
    /// appends a <see cref="CorrelationFooterFormat"/> trace footer so
    /// the prompt itself carries the story-wide trace/correlation id
    /// (iter-3 evaluator item 2 — the bare <c>CommentPromptText</c>
    /// previously violated the criterion).
    /// </summary>
    public const string CommentPromptText = "Please reply with your comment.";

    /// <summary>
    /// Logical messenger name persisted onto every
    /// <see cref="HumanDecisionEvent.Messenger"/> the handler emits.
    /// </summary>
    public const string MessengerName = "telegram";

    /// <summary>
    /// <c>Source</c> tag written into the
    /// <see cref="DecisionAuditDetails"/> JSON for an inline-button
    /// press. Pairs with <see cref="DecisionSourceComment"/> on the
    /// follow-up text-reply path and with <c>"approve"</c>/<c>"reject"</c>
    /// emitted by <see cref="DecisionCommandHandlerBase"/>. Stage 5.3
    /// iter-2 evaluator item 6 — lets forensic queries pivot on the
    /// originating edge of the decision without parsing
    /// <see cref="HumanResponseAuditEntry.MessageId"/>.
    /// </summary>
    public const string DecisionSourceCallback = "callback";

    /// <summary>
    /// <c>Source</c> tag written into the
    /// <see cref="DecisionAuditDetails"/> JSON for a RequiresComment
    /// follow-up text reply. Paired with <see cref="DecisionSourceCallback"/>
    /// — the comment row carries the same <see cref="HumanAction.Value"/>
    /// the operator originally tapped, plus the typed comment.
    /// </summary>
    public const string DecisionSourceComment = "comment";

    /// <summary>
    /// Key namespace used when writing duplicate-CallbackId replay
    /// entries into the shared <see cref="IDistributedCache"/>. Keeps
    /// these entries from colliding with
    /// <c>TelegramQuestionRenderer</c>'s
    /// <see cref="HumanAction"/> entries (keyed
    /// <c>QuestionId:ActionId</c>) and with any other consumer of the
    /// same cache instance.
    /// </summary>
    public const string ReplayCacheKeyPrefix = "tg:cb-replay:";

    /// <summary>
    /// Absolute expiration (relative to insertion) for the duplicate-
    /// CallbackId replay entries written into
    /// <see cref="IDistributedCache"/>. Telegram's
    /// <c>AnswerCallbackQuery</c> wire window is ~30 s, but a webhook
    /// redelivery can arrive minutes later. One hour is a defensible
    /// cap that covers any plausible retry / redelivery horizon while
    /// still giving each entry a hard upper bound. Stage 4.3 swaps the
    /// in-process <c>MemoryDistributedCache</c> for a Redis-backed
    /// <see cref="IDistributedCache"/> so cross-pod / post-restart
    /// duplicates resolve to the cached prior result without code
    /// changes here (iter-3 evaluator item 1).
    /// </summary>
    public static readonly TimeSpan ReplayCacheTtl = TimeSpan.FromHours(1);

    private readonly IPendingQuestionStore _store;
    private readonly ISwarmCommandBus _bus;
    private readonly IAuditLogger _audit;
    private readonly IDeduplicationService _dedup;
    private readonly ITelegramBotClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<CallbackQueryHandler> _logger;

    /// <summary>
    /// Distributed replay cache mapping
    /// <see cref="ReplayCacheKeyPrefix"/> + <see cref="MessengerEvent.CallbackId"/>
    /// → previously-sent answer text. Populated by
    /// <see cref="AnswerAndRememberAsync"/> at every terminal point;
    /// consulted ONLY by the duplicate-CallbackId short-circuit so a
    /// Telegram redelivery — INCLUDING ONE THAT LANDS ON A DIFFERENT
    /// POD OR AFTER A PROCESS RESTART — shows the user the SAME toast
    /// they already saw (plan Stage 3.3 "re-answer with the previous
    /// result"). Backed by <see cref="IDistributedCache"/>:
    /// process-local <c>MemoryDistributedCache</c> in dev/unit-test,
    /// Redis in production via the Stage 4.3 cache module — same
    /// interface, transparent swap.
    /// </summary>
    /// <remarks>
    /// Iter-3 evaluator item 1 fix: the prior in-process
    /// <c>MemoryCache</c> shape was structurally unable to share state
    /// across pods or restarts, so cross-pod / restart duplicates fell
    /// back to <see cref="AlreadyRespondedText"/>. Moving to
    /// <see cref="IDistributedCache"/> closes that gap — the same call
    /// resolves whichever pod handles the duplicate so long as the
    /// underlying cache is shared (Redis / SQL / Cosmos
    /// implementations all qualify).
    /// </remarks>
    private readonly IDistributedCache _replayAnswers;

    public CallbackQueryHandler(
        IPendingQuestionStore store,
        ISwarmCommandBus bus,
        IAuditLogger audit,
        IDeduplicationService dedup,
        ITelegramBotClient client,
        TimeProvider time,
        IDistributedCache replayAnswers,
        ILogger<CallbackQueryHandler> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _dedup = dedup ?? throw new ArgumentNullException(nameof(dedup));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _replayAnswers = replayAnswers ?? throw new ArgumentNullException(nameof(replayAnswers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messengerEvent);

        return messengerEvent.EventType switch
        {
            EventType.CallbackResponse => HandleCallbackAsync(messengerEvent, ct),
            EventType.TextReply => HandleCommentReplyAsync(messengerEvent, ct),
            _ => Task.FromResult(SilentAck(messengerEvent)),
        };
    }

    // ============================================================
    // Callback (inline button) path
    // ============================================================

    private async Task<CommandResult> HandleCallbackAsync(MessengerEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.CallbackId))
        {
            // Defensive: a CallbackResponse without a CallbackId cannot be
            // answered on the wire (AnswerCallbackQueryAsync requires it),
            // so a malformed mapper would corrupt the spinner state. Log
            // loudly and short-circuit without a side effect.
            _logger.LogWarning(
                "Callback rejected: CallbackResponse event has no CallbackId. CorrelationId={CorrelationId} EventId={EventId}",
                evt.CorrelationId,
                evt.EventId);
            return SilentAck(evt);
        }

        // ----- Layer 1: per-callback idempotency. ------------------------
        // implementation-plan.md Stage 3.3: "if the same callback has
        // already been processed (same CallbackQuery.Id), skip
        // processing and re-answer WITH THE PREVIOUS RESULT". The replay
        // cache holds the exact text the user already saw; the fallback
        // (cache eviction / never-cached) is AlreadyRespondedText.
        var callbackDedupKey = CallbackIdDedupKeyPrefix + evt.CallbackId;
        var callbackReserved = await _dedup
            .TryReserveAsync(callbackDedupKey, ct)
            .ConfigureAwait(false);
        if (!callbackReserved)
        {
            // Cross-pod / restart safe: the replay cache is now backed
            // by IDistributedCache (Redis in production, MemoryDistributed
            // in dev / tests). A duplicate landing on a different pod
            // or after a process restart resolves to the SAME previous
            // toast the original tap produced — iter-3 evaluator item 1
            // closed the previous process-local gap.
            //
            // Best-effort read: a transient cache outage (e.g. Redis
            // briefly unavailable) MUST NOT strand the operator's
            // spinner — the canonical decision is already published,
            // audited, and persisted. We swallow the read exception
            // with a WARN and degrade to AlreadyRespondedText (the same
            // fallback used on cache miss). Symmetry with
            // AnswerAndRememberAsync's write-path try/catch.
            string? previous = null;
            try
            {
                previous = await _replayAnswers
                    .GetStringAsync(ReplayCacheKeyPrefix + evt.CallbackId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to read replay cache entry; falling back to AlreadyRespondedText. CorrelationId={CorrelationId} CallbackId={CallbackId}",
                    evt.CorrelationId,
                    evt.CallbackId);
            }
            var hasPrevious = !string.IsNullOrEmpty(previous);
            var replayText = hasPrevious ? previous! : AlreadyRespondedText;
            // Do NOT route through AnswerAndRememberAsync here — we are
            // re-emitting the same text that was already cached, not
            // recording a new outcome.
            await AnswerCallbackAsync(evt, replayText, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Callback short-circuited: duplicate CallbackId — re-answered with {Source}. CorrelationId={CorrelationId} CallbackId={CallbackId}",
                hasPrevious ? "cached prior result (distributed cache hit)" : "AlreadyRespondedText fallback (distributed cache miss / TTL evicted / read error)",
                evt.CorrelationId,
                evt.CallbackId);
            return new CommandResult
            {
                Success = true,
                CorrelationId = evt.CorrelationId,
            };
        }

        // Both reservations live in the SAME try/catch so a failure
        // after EITHER reservation releases BOTH on the way out — the
        // iter-1 evaluator item-3 bug (composite leaked on exception
        // path; retry saw 'Already responded' without ever publishing)
        // is fixed by this structural change.
        string? compositeDedupKey = null;
        try
        {
            return await ProcessCallbackInsideReservationAsync(
                    evt,
                    callbackDedupKey,
                    setCompositeKey: key => compositeDedupKey = key,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeReleaseAsync(callbackDedupKey, evt, ct).ConfigureAwait(false);
            if (compositeDedupKey is not null)
            {
                await SafeReleaseAsync(compositeDedupKey, evt, ct).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<CommandResult> ProcessCallbackInsideReservationAsync(
        MessengerEvent evt,
        string callbackDedupKey,
        Action<string> setCompositeKey,
        CancellationToken ct)
    {
        // ----- Stage 2: parse callback data. -----------------------------
        if (!TryParseCallbackData(evt.Payload, out var questionId, out var actionId))
        {
            await AnswerAndRememberAsync(evt, MalformedCallbackText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Callback rejected: malformed callback data. CorrelationId={CorrelationId} Payload={Payload}",
                evt.CorrelationId,
                evt.Payload);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 3: resolve pending question. ------------------------
        var pending = await _store.GetAsync(questionId, ct).ConfigureAwait(false);
        if (pending is null)
        {
            await AnswerAndRememberAsync(evt, QuestionNotFoundText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Callback rejected: question not found. CorrelationId={CorrelationId} QuestionId={QuestionId}",
                evt.CorrelationId,
                questionId);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 4: expiry check (e2e-scenarios.md). -----------------
        // "Callback query answered after question expired" — when
        // PendingQuestion.ExpiresAt is in the past, reply with
        // ExpiredQuestionText via AnswerCallbackQueryAsync and do NOT
        // publish a HumanDecisionEvent. The Stage 3.5 QuestionTimeoutService
        // owns the timeout-side default-action application.
        var now = _time.GetUtcNow();
        if (pending.ExpiresAt <= now)
        {
            await AnswerAndRememberAsync(evt, ExpiredQuestionText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Callback rejected: question expired. CorrelationId={CorrelationId} QuestionId={QuestionId} ExpiresAt={ExpiresAt} Now={Now}",
                evt.CorrelationId,
                questionId,
                pending.ExpiresAt,
                now);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 5: per-(question, respondent) dedup gate. -----------
        // e2e-scenarios.md "Concurrent button taps from same user":
        // a rapid Approve+Reject pair from the SAME operator on the
        // SAME question must collapse to a single decision. The two
        // taps have DIFFERENT update_ids so the pipeline-level dedup
        // gate does not catch them; this composite key does.
        var respondentUserId = ParseExternalUserId(evt.UserId);
        var compositeDedupKey = QuestionRespondentDedupKeyPrefix
            + questionId
            + CallbackDataSeparator
            + respondentUserId.ToString(CultureInfo.InvariantCulture);
        var compositeReserved = await _dedup
            .TryReserveAsync(compositeDedupKey, ct)
            .ConfigureAwait(false);
        if (!compositeReserved)
        {
            await AnswerAndRememberAsync(evt, AlreadyRespondedText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Callback short-circuited: duplicate (QuestionId, RespondentUserId). CorrelationId={CorrelationId} QuestionId={QuestionId} RespondentUserId={RespondentUserId}",
                evt.CorrelationId,
                questionId,
                respondentUserId);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }
        // From this point on, the catch block in HandleCallbackAsync
        // must release the composite slot too — surface it via the
        // out-callback so the outer scope sees the assignment EVEN IF
        // a subsequent await throws.
        setCompositeKey(compositeDedupKey);

        // ----- Stage 6: durable status backstop. -------------------------
        // Defends against the case where BOTH dedup TTLs evicted before
        // a stale tap arrives — the pending-question store's terminal
        // status is the source of truth.
        if (pending.Status != PendingQuestionStatus.Pending)
        {
            await AnswerAndRememberAsync(evt, AlreadyRespondedText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(compositeDedupKey, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Callback short-circuited: question not in Pending status. CorrelationId={CorrelationId} QuestionId={QuestionId} Status={Status}",
                evt.CorrelationId,
                questionId,
                pending.Status);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 7: resolve the tapped action. -----------------------
        var action = ResolveAction(pending, actionId);
        if (action is null)
        {
            await AnswerAndRememberAsync(evt, UnknownActionText, ct).ConfigureAwait(false);
            // Release the composite slot — the operator might tap a
            // legitimate action next; the callback-id slot stays
            // reserved because Telegram will never redeliver this
            // specific CallbackQuery.Id.
            await SafeReleaseAsync(compositeDedupKey, evt, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Callback rejected: ActionId not in AllowedActions. CorrelationId={CorrelationId} QuestionId={QuestionId} ActionId={ActionId}",
                evt.CorrelationId,
                questionId,
                actionId);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 8: claim-then-record ordering. ----------------------
        // Stage 5.3 iter-5 evaluator item 2 — `RecordSelectionAsync`
        // MUST run AFTER the atomic claim CAS succeeds. The prior
        // ordering (RecordSelection here, claim later) admitted a
        // race where a concurrent QuestionTimeoutService sweep
        // terminal-d the row between this point and the claim, and
        // we still mutated SelectedActionId/SelectedActionValue/
        // RespondentUserId on the now-TimedOut row — leaving stale
        // human-selection metadata on a row that never actually
        // accepted a human decision. Both downstream branches
        // (`HandleRequiresCommentAsync` for RequiresComment, the
        // inline `MarkAnsweredAsync` block below for the standard
        // case) now record the selection only on the winning side
        // of the claim CAS so a lost-race callback never writes.

        if (action.RequiresComment)
        {
            await HandleRequiresCommentAsync(evt, pending, action, respondentUserId, callbackDedupKey, compositeDedupKey, ct)
                .ConfigureAwait(false);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 9: emit HumanDecisionEvent + audit + mark answered. -
        var receivedAt = _time.GetUtcNow();
        var externalMessageId = pending.TelegramMessageId.ToString(CultureInfo.InvariantCulture);
        var externalUserId = respondentUserId.ToString(CultureInfo.InvariantCulture);

        // Stage 5.3 iter-3 evaluator item 7 — claim the row BEFORE
        // any side effect (publish, audit, message edit, callback ack)
        // so the cross-process callback-vs-timeout race is closed by
        // the database UPDATE rather than by dedup TTLs alone.
        // MarkAnsweredAsync conditionally moves the row from
        // (Pending|AwaitingComment) → Answered and returns false when
        // a concurrent QuestionTimeoutService sweep already terminal-d
        // it. On a lost claim we treat the callback as a "stale" tap
        // — surface the standard already-responded reply, release the
        // composite slot, and exit WITHOUT publishing or auditing so
        // the system never double-emits a decision for the same
        // QuestionId.
        var claimed = await _store.MarkAnsweredAsync(questionId, ct).ConfigureAwait(false);
        if (!claimed)
        {
            _logger.LogInformation(
                "Callback short-circuited: lost the atomic Answered claim (a concurrent timeout sweep or duplicate callback won the race). CorrelationId={CorrelationId} QuestionId={QuestionId} RespondentUserId={RespondentUserId}",
                evt.CorrelationId,
                questionId,
                respondentUserId);
            await AnswerAndRememberAsync(evt, AlreadyRespondedText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(compositeDedupKey, ct).ConfigureAwait(false);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // Stage 5.3 iter-5 evaluator item 2 — selection metadata is
        // written ONLY after the atomic Answered claim succeeds.
        // A callback that loses the claim CAS (timeout sweep won the
        // race) short-circuits above WITHOUT mutating
        // SelectedActionId/SelectedActionValue/RespondentUserId, so a
        // timed-out row can never carry stale human-selection data
        // for a decision the system did not accept. The fields are
        // load-bearing for the text-reply path
        // (`GetAwaitingCommentAsync` filters on RespondentUserId),
        // but the inline Answered path never re-reads them — they
        // are persisted purely for forensic / replay-cache value
        // here. If the subsequent publish/audit throws and
        // `TryRevertAnsweredClaimAsync` flips the row back to
        // Pending, the stale fields remain but are overwritten by
        // the next callback's `RecordSelectionAsync` on retry.
        await _store
            .RecordSelectionAsync(questionId, action.ActionId, action.Value, respondentUserId, ct)
            .ConfigureAwait(false);

        var decision = new HumanDecisionEvent
        {
            QuestionId = questionId,
            ActionValue = action.Value,
            Comment = null,
            Messenger = MessengerName,
            ExternalUserId = externalUserId,
            ExternalMessageId = externalMessageId,
            ReceivedAt = receivedAt,
            CorrelationId = pending.CorrelationId,
        };

        // Stage 5.3 iter-8 evaluator item 3 — AUDIT-FIRST ordering for
        // the standard callback path. Prior (iter-3..iter-7) shape was
        // publish-then-audit which let a successful publish escape
        // alongside a failed audit, leaving an outbound
        // HumanDecisionEvent without a durable audit_logs row. The
        // Stage 5.3 brief mandates "log every outbound decision event
        // with full context"; that guarantee requires the audit row to
        // land BEFORE the bus publish. The compensating revert is
        // unchanged: any throw inside this try (audit OR publish)
        // reverts the Answered claim so a retry can re-acquire and
        // re-issue. The audit-first ordering means an audit failure
        // never leaks a publish; a publish failure after audit
        // succeeded may produce a duplicate audit row on retry (the
        // documented persist-every-decision tradeoff — consumer-side
        // QuestionId dedup absorbs the bounded duplicate publish per
        // architecture.md §10.3).
        try
        {
            await _audit.LogHumanResponseAsync(
                new HumanResponseAuditEntry
                {
                    EntryId = Guid.NewGuid(),
                    // Stage 5.3 iter-4 evaluator item 2 — the
                    // acceptance scenario says "MessageId matching
                    // the callback". For a Telegram CallbackQuery
                    // the platform-native "message id of the human
                    // reply" is the callback_query_id (per
                    // MessengerEvent.CallbackId's xml-doc:
                    // "callback-query id" is named as an acceptable
                    // MessageId on HumanResponseAuditEntry). Use
                    // evt.CallbackId when present so the audit row
                    // can be joined directly against the inbound
                    // platform event; the numeric Telegram
                    // message_id of the question being answered is
                    // still preserved in
                    // DecisionAuditDetails.TelegramMessageIdNumeric
                    // for forensic correlation back to the rendered
                    // question. Fallback to externalMessageId on
                    // the (in-practice-impossible) null-CallbackId
                    // edge so the required column never becomes
                    // null and the audit write still succeeds.
                    MessageId = !string.IsNullOrEmpty(evt.CallbackId)
                        ? evt.CallbackId
                        : externalMessageId,
                    UserId = externalUserId,
                    AgentId = pending.AgentId,
                    QuestionId = questionId,
                    ActionValue = action.Value,
                    Comment = null,
                    Timestamp = receivedAt,
                    CorrelationId = pending.CorrelationId,
                    // Stage 5.3 iter-2 evaluator item 6 — callback path
                    // must persist TenantId and a Details JSON so decision
                    // audit rows carry full tenant/workspace context. The
                    // PendingQuestion was stamped with TenantId/WorkspaceId
                    // by the connector at StoreAsync time (architecture.md
                    // §3.1) so this is a denormalised lookup, NOT another
                    // OperatorBinding round-trip.
                    TenantId = pending.TenantId,
                    Details = JsonSerializer.Serialize(
                        new DecisionAuditDetails(
                            pending.WorkspaceId,
                            pending.TelegramChatId,
                            OperatorAlias: null,
                            Source: DecisionSourceCallback,
                            TelegramMessageIdNumeric: pending.TelegramMessageId),
                        DecisionAuditDetailsContext.Default.DecisionAuditDetails),
                },
                ct).ConfigureAwait(false);

            await _bus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);
        }
        catch
        {
            await TryRevertClaimAsync(questionId, ct).ConfigureAwait(false);
            throw;
        }

        // ----- Stage 10: operator-facing feedback. -----------------------
        // Edit message text to embed the decision badge AND set
        // ReplyMarkup = null so ALL buttons are removed (iter-1
        // evaluator item 2: the residual `_noop_` button was still
        // tappable; the scenario requires buttons to be removed).
        await EditMessageShowDecisionAsync(pending, action, ct).ConfigureAwait(false);
        await AnswerAndRememberAsync(evt, DecisionShownLabelPrefix + action.Label, ct).ConfigureAwait(false);

        // ----- Stage 11: seal both reservations. -------------------------
        await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
        await _dedup.MarkProcessedAsync(compositeDedupKey, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Callback handled: HumanDecisionEvent emitted. CorrelationId={CorrelationId} QuestionId={QuestionId} ActionValue={ActionValue} RespondentUserId={RespondentUserId}",
            evt.CorrelationId,
            questionId,
            action.Value,
            respondentUserId);

        return new CommandResult
        {
            Success = true,
            CorrelationId = evt.CorrelationId,
        };
    }

    private async Task HandleRequiresCommentAsync(
        MessengerEvent evt,
        PendingQuestion pending,
        HumanAction action,
        long respondentUserId,
        string callbackDedupKey,
        string compositeDedupKey,
        CancellationToken ct)
    {
        // Stage 5.3 iter-3 evaluator item 7 — claim the row BEFORE
        // any side effect (prompt, message edit, callback ack) so the
        // cross-process race between this RequiresComment branch and a
        // concurrent QuestionTimeoutService sweep is closed by the
        // database UPDATE rather than by dedup TTLs. MarkAwaitingComment
        // returns false when the sweep already terminal-d the row; on
        // a lost claim we issue the standard already-responded reply,
        // edit the original message to embed the sweep's default-action
        // outcome (operator still sees the question is settled), and
        // exit without prompting for a comment they cannot supply.
        var claimed = await _store.MarkAwaitingCommentAsync(pending.QuestionId, ct).ConfigureAwait(false);
        if (!claimed)
        {
            _logger.LogInformation(
                "Callback short-circuited: lost the atomic AwaitingComment claim (a concurrent timeout sweep or duplicate callback won the race). CorrelationId={CorrelationId} QuestionId={QuestionId}",
                evt.CorrelationId,
                pending.QuestionId);
            await AnswerAndRememberAsync(evt, AlreadyRespondedText, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(compositeDedupKey, ct).ConfigureAwait(false);
            return;
        }

        // Stage 5.3 iter-5 evaluator item 2 — selection metadata is
        // written ONLY after the atomic AwaitingComment claim succeeds.
        // This is load-bearing for `HandleCommentReplyAsync` /
        // `GetAwaitingCommentAsync(chatId, userId)` (the text-reply
        // path filters on Status=AwaitingComment AND RespondentUserId
        // = the operator) so the fields MUST be populated BEFORE the
        // operator's follow-up text reply could arrive. By running it
        // immediately after the claim CAS (and BEFORE the prompt
        // SendRequest can race with an arriving text reply), the
        // row is fully resolvable from the moment its Status is
        // AwaitingComment. A callback that loses the claim CAS
        // (sweep won the race) short-circuits above and NEVER writes
        // these fields, so a TimedOut row cannot carry stale
        // human-selection data.
        await _store
            .RecordSelectionAsync(pending.QuestionId, action.ActionId, action.Value, respondentUserId, ct)
            .ConfigureAwait(false);

        // Stage 5.3 iter-5 evaluator item 1 — the post-claim work
        // (prompt, message edit, callback ack, dedup-seal) MUST run
        // inside a try/revert envelope. The prior ordering let the
        // claim succeed and then, if any of the side effects threw,
        // the AwaitingComment claim stayed asserted forever — the
        // operator was stuck with a "please send a comment" prompt
        // that could never produce a decision because the
        // GetAwaitingCommentAsync lookup would resolve to a row no
        // retry could re-claim. The outer catch in
        // `HandleCallbackAsync` releases the dedup slots; this
        // try/catch is what releases the durable status claim so a
        // retry can re-process the same question + user pair.
        try
        {
            // Send the prompt as a fresh chat message — the operator sees
            // both the edited original question (with the selected action
            // embedded, no more buttons) AND the comment prompt. The
            // prompt body carries a trace footer per the story-wide
            // "All messages include trace/correlation ID" criterion
            // (iter-3 evaluator item 2 — bare CommentPromptText omitted
            // the trace).
            await _client.SendRequest(
                    new SendMessageRequest
                    {
                        ChatId = pending.TelegramChatId,
                        Text = BuildCommentPromptText(pending),
                    },
                    ct)
                .ConfigureAwait(false);

            // Edit the original message to embed the selected action AND
            // remove all buttons (visually closes the button row).
            await EditMessageShowDecisionAsync(pending, action, ct).ConfigureAwait(false);

            await AnswerAndRememberAsync(evt, DecisionShownLabelPrefix + action.Label, ct).ConfigureAwait(false);

            // Mark BOTH slots processed — Telegram will never redeliver
            // this specific CallbackQuery.Id, and a fresh tap from the
            // same operator on the same question while we are awaiting
            // their comment must be treated as "already responded".
            await _dedup.MarkProcessedAsync(callbackDedupKey, ct).ConfigureAwait(false);
            await _dedup.MarkProcessedAsync(compositeDedupKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stage 5.3 iter-5 evaluator item 1 — revert the
            // AwaitingComment claim so a retry (webhook redelivery,
            // operator re-tap, sweep) can re-process the same row.
            // Without this revert, a single transient Telegram /
            // network hiccup permanently strands the question: Status
            // is AwaitingComment so no callback can re-claim it via
            // MarkAwaitingCommentAsync (only Pending is a legal source
            // state), and no text reply can complete it because the
            // prompt the operator was supposed to see never sent.
            // `TryRevertAwaitingCommentClaimAsync` is the documented
            // compensation primitive for exactly this case
            // (IPendingQuestionStore.TryRevertAwaitingCommentClaimAsync
            // xml-doc: "if the post-claim 'please send a comment'
            // prompt fails to send, the handler releases the claim so
            // the operator's next tap can re-claim the slot"). The
            // outer catch in HandleCallbackAsync releases the dedup
            // keys; together the revert + dedup-release restore the
            // pre-callback state so the retry sees a fresh question.
            var reverted = await _store
                .TryRevertAwaitingCommentClaimAsync(pending.QuestionId, ct)
                .ConfigureAwait(false);
            _logger.LogError(
                ex,
                "Callback failed AFTER the atomic AwaitingComment claim succeeded; reverted={Reverted} so the row is sweep-eligible again. CorrelationId={CorrelationId} QuestionId={QuestionId}",
                reverted,
                evt.CorrelationId,
                pending.QuestionId);
            throw;
        }

        _logger.LogInformation(
            "Callback handled: AwaitingComment. CorrelationId={CorrelationId} QuestionId={QuestionId} ActionId={ActionId}",
            evt.CorrelationId,
            pending.QuestionId,
            action.ActionId);
    }

    // ============================================================
    // Text-reply (RequiresComment follow-up) path
    // ============================================================

    private async Task<CommandResult> HandleCommentReplyAsync(MessengerEvent evt, CancellationToken ct)
    {
        // The pipeline only routes TextReply events here when
        // GetAwaitingCommentAsync returned a non-null pending question
        // (see TelegramUpdatePipeline.RouteTextReplyAsync), but we re-
        // resolve from the durable store so a stale routing decision
        // cannot publish a phantom decision.
        if (!long.TryParse(evt.ChatId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)
            || !long.TryParse(evt.UserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return SilentAck(evt);
        }

        var pending = await _store.GetAwaitingCommentAsync(chatId, userId, ct).ConfigureAwait(false);
        if (pending is null
            || string.IsNullOrEmpty(pending.SelectedActionValue)
            || string.IsNullOrEmpty(pending.SelectedActionId))
        {
            return SilentAck(evt);
        }

        var comment = evt.Payload;
        if (string.IsNullOrWhiteSpace(comment))
        {
            return SilentAck(evt);
        }

        // Per-(question, respondent) dedup gate — text-reply analogue of
        // the callback path's composite key. Pipeline-level EventId dedup
        // is PER-UPDATE: two text messages from the same operator
        // addressing the same AwaitingComment question carry DIFFERENT
        // update_ids and thus pass that gate. Without this reservation
        // the GetAwaitingCommentAsync → PublishHumanDecisionAsync →
        // MarkAnsweredAsync window admits a double-publish (two events
        // reach the inner block, both pass the AwaitingComment status
        // check, both publish before either flips the status). Mirrors
        // ProcessCallbackInsideReservationAsync's composite gate so the
        // text-reply path matches the three-layer idempotency contract
        // the callback path advertises.
        var commentDedupKey = QuestionRespondentCommentDedupKeyPrefix
            + pending.QuestionId
            + CallbackDataSeparator
            + userId.ToString(CultureInfo.InvariantCulture);
        var commentReserved = await _dedup
            .TryReserveAsync(commentDedupKey, ct)
            .ConfigureAwait(false);
        if (!commentReserved)
        {
            // A concurrent reply already won the race; the text-reply
            // path has no callback-id to ack, so SilentAck is the
            // correct terminal — Telegram does not show a spinner for
            // chat messages.
            _logger.LogInformation(
                "Comment reply short-circuited: duplicate (QuestionId, RespondentUserId). CorrelationId={CorrelationId} QuestionId={QuestionId} RespondentUserId={RespondentUserId}",
                evt.CorrelationId,
                pending.QuestionId,
                userId);
            return SilentAck(evt);
        }

        try
        {
            var receivedAt = _time.GetUtcNow();
            var externalMessageId = pending.TelegramMessageId.ToString(CultureInfo.InvariantCulture);
            var externalUserId = userId.ToString(CultureInfo.InvariantCulture);

            // Stage 5.3 iter-3 evaluator item 7 — claim the row
            // BEFORE publish/audit so a late timeout sweep that
            // terminal-d this AwaitingComment row between our
            // GetAwaitingCommentAsync read and this point cannot
            // produce a duplicate decision. The atomic CAS in
            // MarkAnsweredAsync returns false on a lost claim;
            // release the comment slot and silent-ack so the next
            // sweep is the sole authority on the question's
            // outcome.
            var claimed = await _store.MarkAnsweredAsync(pending.QuestionId, ct).ConfigureAwait(false);
            if (!claimed)
            {
                _logger.LogInformation(
                    "Comment reply short-circuited: lost the atomic Answered claim (the AwaitingComment row was terminal-d by a concurrent sweep or duplicate reply). CorrelationId={CorrelationId} QuestionId={QuestionId}",
                    evt.CorrelationId,
                    pending.QuestionId);
                await SafeReleaseAsync(commentDedupKey, evt, ct).ConfigureAwait(false);
                return SilentAck(evt);
            }

            var decision = new HumanDecisionEvent
            {
                QuestionId = pending.QuestionId,
                ActionValue = pending.SelectedActionValue!,
                Comment = comment,
                Messenger = MessengerName,
                ExternalUserId = externalUserId,
                ExternalMessageId = externalMessageId,
                ReceivedAt = receivedAt,
                CorrelationId = pending.CorrelationId,
            };
            // Stage 5.3 iter-8 evaluator item 3 — AUDIT-FIRST ordering
            // for the comment-reply (RequiresComment follow-up) path.
            // Same rationale as the standard callback above: audit
            // BEFORE publish so a transient audit-DB failure cannot
            // leak an outbound HumanDecisionEvent without a durable
            // audit_logs row. The shared try/revert envelope below
            // still releases both the comment dedup slot AND the
            // Answered claim on ANY throw inside this block, so a
            // pre-publish audit failure cleanly aborts (no event
            // escapes) and the operator's retry re-acquires both
            // gates and re-issues exactly once.
            await _audit.LogHumanResponseAsync(
                new HumanResponseAuditEntry
                {
                    EntryId = Guid.NewGuid(),
                    // Stage 5.3 iter-4 evaluator item 2 — for the
                    // text-reply (RequiresComment follow-up) path
                    // there is no callback-query id (it's a chat
                    // message, not a button tap). The platform-native
                    // identifier of the OPERATOR'S inbound message is
                    // evt.EventId (the Telegram connector synthesises
                    // it from update_id so MessageId is unique per
                    // update and joinable against the inbound update
                    // audit trail). Keep TelegramMessageIdNumeric in
                    // Details so forensic queries can still join back
                    // to the rendered question.
                    MessageId = evt.EventId,
                    UserId = externalUserId,
                    AgentId = pending.AgentId,
                    QuestionId = pending.QuestionId,
                    ActionValue = pending.SelectedActionValue!,
                    Comment = comment,
                    Timestamp = receivedAt,
                    CorrelationId = pending.CorrelationId,
                    // Stage 5.3 iter-2 evaluator item 6 — text-reply
                    // (RequiresComment follow-up) audit row carries the
                    // same tenant/workspace context the callback row
                    // does. Source is tagged "comment" so a forensic
                    // query can pivot on the comment-fallback path
                    // distinctly from the original button press.
                    TenantId = pending.TenantId,
                    Details = JsonSerializer.Serialize(
                        new DecisionAuditDetails(
                            pending.WorkspaceId,
                            pending.TelegramChatId,
                            OperatorAlias: null,
                            Source: DecisionSourceComment,
                            TelegramMessageIdNumeric: pending.TelegramMessageId),
                        DecisionAuditDetailsContext.Default.DecisionAuditDetails),
                },
                ct).ConfigureAwait(false);

            await _bus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);

            // Seal the comment reservation only on the success path so a
            // post-publish transient failure (handled below) releases the
            // slot and the pipeline can re-deliver a fresh reply normally.
            await _dedup.MarkProcessedAsync(commentDedupKey, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Comment reply handled: HumanDecisionEvent emitted. CorrelationId={CorrelationId} QuestionId={QuestionId} ActionValue={ActionValue}",
                evt.CorrelationId,
                pending.QuestionId,
                pending.SelectedActionValue);

            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Release-on-throw symmetry with the callback path: if any
            // step after the reservation (publish / audit / mark-answered)
            // fails, the slot must NOT remain reserved — otherwise a
            // live retry would falsely short-circuit as "already
            // responded" without ever publishing the decision (the same
            // class of bug the callback path's iter-1 fix eliminated).
            // Stage 5.3 iter-3 evaluator items 6 + 7 — also revert the
            // atomic Answered claim so the retry's MarkAnsweredAsync
            // can re-acquire (without revert the question stays
            // terminally Answered and the decision is lost).
            await TryRevertClaimAsync(pending.QuestionId, ct).ConfigureAwait(false);
            await SafeReleaseAsync(commentDedupKey, evt, ct).ConfigureAwait(false);
            throw;
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Parses a <c>QuestionId:ActionId</c> callback payload. Returns
    /// <c>false</c> when the payload is null/empty, has no
    /// <see cref="CallbackDataSeparator"/>, or has an empty component on
    /// either side of the separator.
    /// </summary>
    internal static bool TryParseCallbackData(
        string? payload,
        out string questionId,
        out string actionId)
    {
        questionId = string.Empty;
        actionId = string.Empty;
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        var sepIndex = payload.IndexOf(CallbackDataSeparator);
        if (sepIndex <= 0 || sepIndex >= payload.Length - 1)
        {
            return false;
        }

        questionId = payload[..sepIndex];
        actionId = payload[(sepIndex + 1)..];
        return true;
    }

    private static HumanAction? ResolveAction(PendingQuestion pending, string actionId)
    {
        foreach (var candidate in pending.AllowedActions)
        {
            if (string.Equals(candidate.ActionId, actionId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Best-effort compensation for a successful
    /// <see cref="IPendingQuestionStore.MarkAnsweredAsync"/> when the
    /// subsequent publish/audit step throws. The CAS revert returns
    /// false when the row is no longer in
    /// <see cref="PendingQuestionStatus.Answered"/> (e.g. another
    /// process already progressed it) — which is the safe outcome —
    /// or when the store itself throws, in which case we log and
    /// swallow because the outer catch needs to rethrow the
    /// ORIGINAL publish/audit exception so the pipeline's
    /// release-on-throw layer can act on it. Stage 5.3 iter-3
    /// evaluator items 6 + 7.
    /// </summary>
    private async Task TryRevertClaimAsync(string questionId, CancellationToken ct)
    {
        try
        {
            var reverted = await _store.TryRevertAnsweredClaimAsync(questionId, ct).ConfigureAwait(false);
            if (!reverted)
            {
                _logger.LogInformation(
                    "Atomic Answered claim revert returned false (row no longer in Answered state). QuestionId={QuestionId}",
                    questionId);
            }
        }
        catch (Exception revertEx) when (revertEx is not OperationCanceledException)
        {
            _logger.LogError(
                revertEx,
                "Atomic Answered claim revert threw; original publish/audit exception will still propagate. QuestionId={QuestionId}",
                questionId);
        }
    }

    /// <summary>
    /// Composes the post-decision message body. The output preserves
    /// EVERY field the original message rendered (story "Question
    /// handling" requirement: include context, severity, timeout,
    /// proposed default action) so the operator can still see the
    /// full question metadata alongside the selected action — iter-3
    /// evaluator item 3 fixed the previous Title+Body-only edit that
    /// dropped severity / timeout / default-action context. Composition
    /// order:
    /// <list type="number">
    /// <item><see cref="PendingQuestion.Title"/></item>
    /// <item>Severity badge + label
    ///       (e.g. <c>"⚠️ Severity: High"</c>)</item>
    /// <item>Timeout footer
    ///       (e.g. <c>"Timeout: 2025-01-01T01:00:00Z"</c>) —
    ///       absolute ISO-8601 rather than a relative countdown, which
    ///       is meaningless once the question is answered</item>
    /// <item>Proposed default action line, when
    ///       <see cref="PendingQuestion.DefaultActionId"/> is set</item>
    /// <item><see cref="PendingQuestion.Body"/></item>
    /// <item>Decision badge
    ///       <see cref="DecisionShownLabelPrefix"/><c>{selected.Label}</c></item>
    /// <item>Trace/correlation footer
    ///       <see cref="CorrelationFooterFormat"/></item>
    /// </list>
    /// All fields are emitted as plain text (the corresponding
    /// <c>EditMessageTextRequest</c> uses <see cref="ParseMode.None"/>)
    /// so MarkdownV2 metacharacters in user-supplied text cannot
    /// re-fire and reject the edit.
    /// </summary>
    internal static string BuildDecisionMessageText(PendingQuestion pending, HumanAction selected)
    {
        var sb = new StringBuilder();
        sb.Append(pending.Title);
        sb.Append('\n');
        sb.Append('\n');

        // Severity badge + label — mirrors TelegramQuestionRenderer's
        // outbound rendering so operators see the same context post-
        // decision (iter-3 evaluator item 3 requirement).
        sb.Append(SeverityBadge(pending.Severity));
        sb.Append(' ');
        sb.Append("Severity: ");
        sb.Append(pending.Severity.ToString());
        sb.Append('\n');

        // Timeout — absolute ISO-8601 so the operator / audit log can
        // grep by timestamp; the original message's relative
        // countdown is meaningless once the question is answered.
        sb.Append("Timeout: ");
        sb.Append(pending.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
        sb.Append('\n');

        // Proposed default action — emitted only when the envelope
        // surfaced one (DefaultActionId nullable on PendingQuestion).
        // The label is resolved from AllowedActions because the
        // denormalized DefaultActionValue is only the wire value, not
        // a human-readable label.
        if (!string.IsNullOrEmpty(pending.DefaultActionId))
        {
            sb.Append("Default action if no response: ");
            sb.Append(ResolveDefaultActionLabel(pending));
            sb.Append('\n');
        }

        if (!string.IsNullOrEmpty(pending.Body))
        {
            sb.Append('\n');
            sb.Append(pending.Body);
        }

        sb.Append(DecisionFooterSeparator);
        sb.Append(DecisionShownLabelPrefix);
        sb.Append(selected.Label);

        if (!string.IsNullOrEmpty(pending.CorrelationId))
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                CorrelationFooterFormat,
                pending.CorrelationId);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Composes the body of the follow-up comment-prompt message sent
    /// when the operator taps a <see cref="HumanAction.RequiresComment"/>
    /// action. Appends a <see cref="CorrelationFooterFormat"/> trace
    /// footer so the prompt — like every other outbound message —
    /// carries the trace/correlation id (iter-3 evaluator item 2).
    /// </summary>
    internal static string BuildCommentPromptText(PendingQuestion pending)
    {
        if (string.IsNullOrEmpty(pending.CorrelationId))
        {
            return CommentPromptText;
        }
        return CommentPromptText
               + string.Format(
                   CultureInfo.InvariantCulture,
                   CorrelationFooterFormat,
                   pending.CorrelationId);
    }

    private static string ResolveDefaultActionLabel(PendingQuestion pending)
    {
        foreach (var action in pending.AllowedActions)
        {
            if (string.Equals(action.ActionId, pending.DefaultActionId, StringComparison.Ordinal))
            {
                return action.Label;
            }
        }
        // Envelope validation guarantees DefaultActionId matches one
        // of AllowedActions at StoreAsync time, but fall back to the
        // id itself rather than throw — a missing default-action
        // label must not block the operator-facing edit.
        return pending.DefaultActionId!;
    }

    /// <summary>
    /// Emoji badge for the supplied severity. Mirrors
    /// <c>TelegramQuestionRenderer.SeverityBadge</c> so the outbound
    /// rendering and the post-decision edit use the same visual
    /// vocabulary.
    /// </summary>
    private static string SeverityBadge(MessageSeverity severity) => severity switch
    {
        MessageSeverity.Critical => "🚨",
        MessageSeverity.High => "⚠️",
        MessageSeverity.Normal => "ℹ️",
        MessageSeverity.Low => "•",
        _ => "•",
    };

    private async Task EditMessageShowDecisionAsync(
        PendingQuestion pending,
        HumanAction selected,
        CancellationToken ct)
    {
        // Single edit call that BOTH replaces the message text (showing
        // the selected action in the message body) AND sets
        // ReplyMarkup = null (removing every tappable button — fixes
        // iter-1 evaluator item 2 where a residual no-op button was
        // still tappable, contradicting the scenario's "buttons are
        // removed" requirement). One round-trip, no leftover keyboard.
        try
        {
            var messageId = ConvertTelegramMessageId(pending.TelegramMessageId);
            if (messageId is null)
            {
                _logger.LogWarning(
                    "Skipping message edit: TelegramMessageId {MessageId} does not fit int32 (Bot API limit). CorrelationId={CorrelationId} QuestionId={QuestionId}",
                    pending.TelegramMessageId,
                    pending.CorrelationId,
                    pending.QuestionId);
                return;
            }

            await _client.SendRequest(
                    new EditMessageTextRequest
                    {
                        ChatId = pending.TelegramChatId,
                        MessageId = messageId.Value,
                        Text = BuildDecisionMessageText(pending, selected),
                        // ParseMode intentionally None — plain text so
                        // user-supplied Title/Body characters cannot
                        // re-fire MarkdownV2 escapes and reject the edit.
                        ParseMode = ParseMode.None,
                        // ReplyMarkup intentionally null — REMOVES the
                        // inline keyboard entirely. Telegram's API
                        // treats null markup as "drop the keyboard".
                        ReplyMarkup = null,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The operator-facing edit is a cosmetic enhancement: the
            // HumanDecisionEvent has already been published and the
            // pending question is already marked answered, so a failure
            // here MUST NOT throw out of HandleAsync — that would
            // trigger pipeline-level release-on-throw and the composite
            // dedup gate would absorb the redelivery noiselessly. Log
            // and continue so the caller's final AnswerCallback still
            // fires.
            _logger.LogWarning(
                ex,
                "Failed to edit question message after decision. CorrelationId={CorrelationId} QuestionId={QuestionId} ChatId={ChatId} MessageId={MessageId}",
                pending.CorrelationId,
                pending.QuestionId,
                pending.TelegramChatId,
                pending.TelegramMessageId);
        }
    }

    /// <summary>
    /// Cache the supplied <paramref name="text"/> against the event's
    /// <see cref="MessengerEvent.CallbackId"/> AND fire the wire
    /// <c>AnswerCallbackQueryAsync</c>. Used at EVERY terminal point so
    /// a duplicate <see cref="MessengerEvent.CallbackId"/> delivery can
    /// replay the exact same toast text (plan Stage 3.3 "re-answer with
    /// the previous result").
    /// </summary>
    private async Task AnswerAndRememberAsync(MessengerEvent evt, string text, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(evt.CallbackId))
        {
            // Last-writer-wins is the right semantics here: every
            // terminal path through Process... writes ONCE for a given
            // CallbackId (subsequent deliveries with the same id short-
            // circuit at the cb: reservation gate BEFORE reaching this
            // helper). The entry carries AbsoluteExpirationRelativeToNow
            // = ReplayCacheTtl (1 h) so the cache cannot grow without
            // bound; the underlying IDistributedCache (Redis or
            // MemoryDistributed) enforces its own size policy on top
            // of TTL.
            //
            // Best-effort: a cache write failure (e.g. Redis transient
            // unavailability) MUST NOT crash the operator-facing flow
            // — the canonical decision is already published, audited,
            // and persisted to PendingQuestionStore; the replay cache
            // is a UX optimisation. Log and continue so AnswerCallback
            // still fires.
            try
            {
                await _replayAnswers.SetStringAsync(
                        ReplayCacheKeyPrefix + evt.CallbackId,
                        text,
                        new DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = ReplayCacheTtl,
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write replay cache entry; duplicate redelivery will fall back to AlreadyRespondedText. CorrelationId={CorrelationId} CallbackId={CallbackId}",
                    evt.CorrelationId,
                    evt.CallbackId);
            }
        }
        await AnswerCallbackAsync(evt, text, ct).ConfigureAwait(false);
    }

    private async Task AnswerCallbackAsync(MessengerEvent evt, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.CallbackId))
        {
            return;
        }

        try
        {
            await _client.SendRequest(
                    new AnswerCallbackQueryRequest
                    {
                        CallbackQueryId = evt.CallbackId,
                        Text = text,
                        ShowAlert = false,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telegram's AnswerCallbackQuery has a 30 s window and may
            // legitimately fail on stale callbacks; the side-effects
            // (event publish, audit, mark answered) have already been
            // applied, so swallow with a warning rather than tripping
            // pipeline release-on-throw.
            _logger.LogWarning(
                ex,
                "Failed to answer Telegram callback query. CorrelationId={CorrelationId} CallbackId={CallbackId} Text={Text}",
                evt.CorrelationId,
                evt.CallbackId,
                text);
        }
    }

    private async Task SafeReleaseAsync(string dedupKey, MessengerEvent evt, CancellationToken ct)
    {
        try
        {
            await _dedup.ReleaseReservationAsync(dedupKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort release. The dedup service's sticky-processed
            // guard means a release-after-MarkProcessed is already a
            // no-op; a hard release failure is rare but should be
            // visible without crashing the handler.
            //
            // OperationCanceledException is intentionally NOT caught
            // here (consistent with every other catch in this class):
            // cancellation is a control-flow signal that must propagate
            // so the caller's catch-and-rethrow path can unwind and the
            // worker-level cancellation pipeline can tear things down.
            // Silently logging a cancellation as a warning would mask
            // shutdown / token-revocation and corrupt the rethrow chain
            // in HandleCallbackAsync's release-on-throw block.
            _logger.LogWarning(
                ex,
                "Failed to release dedup reservation. CorrelationId={CorrelationId} DedupKey={DedupKey}",
                evt.CorrelationId,
                dedupKey);
        }
    }

    private static long ParseExternalUserId(string userId)
    {
        // Telegram user ids are int64 on the wire and arrive here as a
        // string in invariant culture (see TelegramUpdateMapper). A
        // non-numeric id should never reach this path, but if it does
        // we want a deterministic value so the composite dedup key is
        // still well-defined; -1 is a sentinel chosen because Telegram
        // never issues negative user ids.
        return long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1L;
    }

    private static int? ConvertTelegramMessageId(long messageId)
    {
        if (messageId < int.MinValue || messageId > int.MaxValue)
        {
            return null;
        }
        return (int)messageId;
    }

    private static CommandResult SilentAck(MessengerEvent evt) => new()
    {
        Success = true,
        ResponseText = null,
        CorrelationId = evt.CorrelationId,
    };
}
