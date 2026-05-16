using System.Globalization;
using System.Text;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
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
/// PublishHumanDecisionAsync, audit, MarkAnsweredAsync), the catch block
/// releases <i>both</i> the per-callback AND the composite slot so a
/// live re-delivery is processed normally — the bug the iter-1 evaluator
/// flagged (composite reservation leaked, retry sees "Already responded"
/// without publishing) is fixed here. Successful completion sticks both
/// slots via <see cref="IDeduplicationService.MarkProcessedAsync"/>.
/// </para>
/// </remarks>
public sealed class CallbackQueryHandler : ICallbackHandler, IDisposable
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
    /// </summary>
    public const string CommentPromptText = "Please reply with your comment.";

    /// <summary>
    /// Logical messenger name persisted onto every
    /// <see cref="HumanDecisionEvent.Messenger"/> the handler emits.
    /// </summary>
    public const string MessengerName = "telegram";

    /// <summary>
    /// Maximum number of cached duplicate-CallbackId replay entries the
    /// in-process <see cref="MemoryCache"/> backing
    /// <see cref="_replayAnswers"/> will hold before LRU compaction
    /// evicts older entries. Bounds the process-local footprint a
    /// long-running bot can accumulate from Telegram callback ids
    /// (iter-2 evaluator item 2 — the previous
    /// <c>ConcurrentDictionary</c> shape was unbounded).
    /// </summary>
    /// <remarks>
    /// 10,000 entries × ~32-byte callback id × ~32-byte answer text =
    /// ~640 KB worst case, which is bounded and recoverable on
    /// process restart. Stage 4.3 will replace this with a distributed
    /// cache shared across pods so the cross-pod / restart fallback to
    /// <see cref="AlreadyRespondedText"/> is closed.
    /// </remarks>
    public const long ReplayCacheMaxSize = 10_000L;

    /// <summary>
    /// Absolute expiration (relative to insertion) for the duplicate-
    /// CallbackId replay cache. Telegram's
    /// <c>AnswerCallbackQuery</c> wire window is ~30 s, but a Telegram
    /// webhook redelivery can arrive minutes later, and the pipeline
    /// dedup TTLs (<see cref="IDeduplicationService"/>) live on a
    /// similar minute-grain horizon. One hour is a defensible cap that
    /// covers any plausible in-process retry window while still giving
    /// the cache a hard upper bound (iter-2 evaluator item 2).
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
    /// Bounded in-memory replay cache mapping
    /// <see cref="MessengerEvent.CallbackId"/> → previously-sent answer
    /// text. Populated by <see cref="AnswerAndRememberAsync"/> at every
    /// terminal point; consulted ONLY by the duplicate-CallbackId
    /// short-circuit so a Telegram redelivery shows the user the SAME
    /// toast they already saw (plan Stage 3.3 "re-answer with the
    /// previous result"). Backed by <see cref="MemoryCache"/> with
    /// <see cref="ReplayCacheMaxSize"/> entry-count cap (LRU eviction
    /// on overflow) and <see cref="ReplayCacheTtl"/> absolute
    /// expiration per entry — iter-2 evaluator item 2 fixed the
    /// previous unbounded <c>ConcurrentDictionary</c> footprint.
    /// </summary>
    /// <remarks>
    /// Process-local — Stage 4.3 will swap a distributed cache in so
    /// cross-pod / restart duplicates can still resolve to the cached
    /// prior result instead of falling back to
    /// <see cref="AlreadyRespondedText"/>. The interface used here
    /// (Set / TryGetValue against string keys) maps 1:1 onto
    /// <c>IDistributedCache</c>, so the swap is a localised change.
    /// </remarks>
    private readonly MemoryCache _replayAnswers = new(new MemoryCacheOptions
    {
        SizeLimit = ReplayCacheMaxSize,
    });

    public CallbackQueryHandler(
        IPendingQuestionStore store,
        ISwarmCommandBus bus,
        IAuditLogger audit,
        IDeduplicationService dedup,
        ITelegramBotClient client,
        TimeProvider time,
        ILogger<CallbackQueryHandler> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _dedup = dedup ?? throw new ArgumentNullException(nameof(dedup));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Disposes the in-process <see cref="MemoryCache"/> backing the
    /// duplicate-CallbackId replay cache. The handler is a singleton
    /// in the DI graph; the container invokes this at root-scope
    /// teardown so the cache's eviction timers / pinned roots are
    /// released cleanly.
    /// </summary>
    public void Dispose()
    {
        _replayAnswers.Dispose();
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
            var hasPrevious = _replayAnswers.TryGetValue(evt.CallbackId, out string? previous)
                              && !string.IsNullOrEmpty(previous);
            var replayText = hasPrevious ? previous! : AlreadyRespondedText;
            // Do NOT route through AnswerAndRememberAsync here — we are
            // re-emitting the same text that was already cached, not
            // recording a new outcome.
            await AnswerCallbackAsync(evt, replayText, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Callback short-circuited: duplicate CallbackId — re-answered with {Source}. CorrelationId={CorrelationId} CallbackId={CallbackId}",
                hasPrevious ? "cached prior result" : "AlreadyRespondedText fallback (replay cache evicted)",
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

        // ----- Stage 8: record the selection on the pending question. ---
        await _store
            .RecordSelectionAsync(questionId, action.ActionId, action.Value, respondentUserId, ct)
            .ConfigureAwait(false);

        if (action.RequiresComment)
        {
            await HandleRequiresCommentAsync(evt, pending, action, callbackDedupKey, compositeDedupKey, ct)
                .ConfigureAwait(false);
            return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
        }

        // ----- Stage 9: emit HumanDecisionEvent + audit + mark answered. -
        var receivedAt = _time.GetUtcNow();
        var externalMessageId = pending.TelegramMessageId.ToString(CultureInfo.InvariantCulture);
        var externalUserId = respondentUserId.ToString(CultureInfo.InvariantCulture);

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
        await _bus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);

        await _audit.LogHumanResponseAsync(
            new HumanResponseAuditEntry
            {
                EntryId = Guid.NewGuid(),
                MessageId = externalMessageId,
                UserId = externalUserId,
                AgentId = pending.AgentId,
                QuestionId = questionId,
                ActionValue = action.Value,
                Comment = null,
                Timestamp = receivedAt,
                CorrelationId = pending.CorrelationId,
            },
            ct).ConfigureAwait(false);

        // Transition AFTER publish+audit so a transient failure leaves
        // the question Pending and re-deliverable (mirrors the
        // DecisionCommandHandlerBase contract).
        await _store.MarkAnsweredAsync(questionId, ct).ConfigureAwait(false);

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
        string callbackDedupKey,
        string compositeDedupKey,
        CancellationToken ct)
    {
        // Transition to AwaitingComment so the pipeline's TextReply
        // routing (GetAwaitingCommentAsync) correlates the follow-up
        // text message back to this question.
        await _store.MarkAwaitingCommentAsync(pending.QuestionId, ct).ConfigureAwait(false);

        // Send the prompt as a fresh chat message — the operator sees
        // both the edited original question (with the selected action
        // embedded, no more buttons) AND the comment prompt.
        await _client.SendRequest(
                new SendMessageRequest
                {
                    ChatId = pending.TelegramChatId,
                    Text = CommentPromptText,
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

        var receivedAt = _time.GetUtcNow();
        var externalMessageId = pending.TelegramMessageId.ToString(CultureInfo.InvariantCulture);
        var externalUserId = userId.ToString(CultureInfo.InvariantCulture);

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
        await _bus.PublishHumanDecisionAsync(decision, ct).ConfigureAwait(false);

        await _audit.LogHumanResponseAsync(
            new HumanResponseAuditEntry
            {
                EntryId = Guid.NewGuid(),
                MessageId = externalMessageId,
                UserId = externalUserId,
                AgentId = pending.AgentId,
                QuestionId = pending.QuestionId,
                ActionValue = pending.SelectedActionValue!,
                Comment = comment,
                Timestamp = receivedAt,
                CorrelationId = pending.CorrelationId,
            },
            ct).ConfigureAwait(false);

        await _store.MarkAnsweredAsync(pending.QuestionId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Comment reply handled: HumanDecisionEvent emitted. CorrelationId={CorrelationId} QuestionId={QuestionId} ActionValue={ActionValue}",
            evt.CorrelationId,
            pending.QuestionId,
            pending.SelectedActionValue);

        return new CommandResult { Success = true, CorrelationId = evt.CorrelationId };
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
    /// Composes the post-decision message body: the original
    /// <see cref="PendingQuestion.Title"/> + <see cref="PendingQuestion.Body"/>
    /// re-stated as plain text (parse mode dropped so MarkdownV2
    /// metacharacters don't re-fire), followed by a
    /// <see cref="DecisionFooterSeparator"/>-delimited decision badge
    /// "<see cref="DecisionShownLabelPrefix"/>{Label}", and finally
    /// the per-message trace/correlation footer
    /// (<see cref="CorrelationFooterFormat"/>) — preserving the story-
    /// wide "All messages include trace/correlation ID" acceptance
    /// criterion through the post-decision edit (iter-2 evaluator
    /// item 1 — the edit previously dropped
    /// <see cref="PendingQuestion.CorrelationId"/>).
    /// </summary>
    internal static string BuildDecisionMessageText(PendingQuestion pending, HumanAction selected)
    {
        var sb = new StringBuilder();
        sb.Append(pending.Title);
        if (!string.IsNullOrEmpty(pending.Body))
        {
            sb.Append("\n\n");
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
            // helper). The MemoryCache entry is sized = 1 so the
            // backing options' SizeLimit (ReplayCacheMaxSize) actually
            // caps entry count; absolute expiration (ReplayCacheTtl)
            // guarantees a hard upper bound on retention even if the
            // cap is never hit (iter-2 evaluator item 2 — bounded TTL
            // + size replaces the previous unbounded dictionary).
            _replayAnswers.Set(
                evt.CallbackId,
                text,
                new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = ReplayCacheTtl,
                });
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
        catch (Exception ex)
        {
            // Best-effort release. The dedup service's sticky-processed
            // guard means a release-after-MarkProcessed is already a
            // no-op; a hard release failure is rare but should be
            // visible without crashing the handler.
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
