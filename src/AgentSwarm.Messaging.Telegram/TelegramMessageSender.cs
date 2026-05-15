using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Stage 2.3 outbound sender. Implements <see cref="IMessageSender"/> from
/// <c>AgentSwarm.Messaging.Core</c> and is the sole owner of question
/// rendering for the Telegram connector (architecture.md §11 row
/// <c>IMessageSender return type</c>).
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities, in order of execution per <see cref="SendQuestionAsync"/>:
/// build the <see cref="InlineKeyboardMarkup"/> for the question's
/// <see cref="AgentQuestion.AllowedActions"/>; write each
/// <see cref="HumanAction"/> to <see cref="IDistributedCache"/> keyed by
/// <c>QuestionId:ActionId</c> with absolute expiry at
/// <see cref="AgentQuestion.ExpiresAt"/> + 5 minutes (the grace window for
/// <c>CallbackQueryHandler</c> per architecture.md §5.2); render the body
/// (severity badge, title, body, optional proposed default action,
/// expires-at, correlation footer) as MarkdownV2; split the body into
/// chunks of ≤ 4096 UTF-16 characters at paragraph/line boundaries when
/// possible; acquire a token from the global + per-chat token-bucket rate
/// limiter (<see cref="ITelegramRateLimiter"/>) before each Telegram
/// API call; transparently retry once on HTTP 429 using the
/// <c>Parameters.RetryAfter</c> hint; and persist the Telegram
/// <c>message_id</c> on success via <see cref="IMessageIdTracker"/> so a
/// follow-up text reply can be correlated back to the originating trace.
/// </para>
/// <para>
/// All wall-clock waits — both rate-limit blocking and 429 backoff — go
/// through <see cref="IDelayProvider"/> so unit tests can stub them out
/// without sleeping. The <see cref="TimeProvider"/> dependency is used
/// only for rendering the expires-at countdown and for the cache-expiry
/// computation; both call sites tolerate a stubbed clock.
/// </para>
/// </remarks>
public sealed class TelegramMessageSender : IMessageSender
{
    /// <summary>
    /// Telegram Bot API hard limit on the <c>text</c> field of
    /// <c>sendMessage</c> — 4 096 UTF-16 code units. We chunk above this
    /// boundary so a single oversize message is delivered as an in-order
    /// sequence rather than rejected.
    /// </summary>
    public const int MaxTelegramMessageLength = 4096;

    /// <summary>
    /// Telegram Bot API hard limit on the <c>callback_data</c> field of
    /// an inline-keyboard button — 64 bytes when UTF-8 encoded
    /// (1–64 bytes inclusive). Exceeding this causes the Bot API to
    /// reject <c>sendMessage</c> with a generic
    /// <see cref="ApiRequestException"/>; the sender validates the
    /// composed <c>QuestionId:ActionId</c> payload up front so callers
    /// get a clear <see cref="ArgumentException"/> rather than a cryptic
    /// API rejection at send time.
    /// </summary>
    public const int MaxCallbackDataBytes = 64;

    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan CacheGracePeriod = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions HumanActionSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ITelegramApiClient _api;
    private readonly ITelegramRateLimiter _rateLimiter;
    private readonly IDistributedCache _cache;
    private readonly IMessageIdTracker _tracker;
    private readonly IDelayProvider _delayProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramMessageSender> _logger;

    public TelegramMessageSender(
        ITelegramApiClient api,
        ITelegramRateLimiter rateLimiter,
        IDistributedCache cache,
        IMessageIdTracker tracker,
        IDelayProvider delayProvider,
        ILogger<TelegramMessageSender> logger,
        TimeProvider? timeProvider = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct)
    {
        // Two-arg legacy overload — no correlation context. Production
        // callers (Stage 4.1 OutboundQueueProcessor) use the
        // SendMessageAsync(long, MessengerMessage, ct) overload below
        // which propagates CorrelationId through the rendered footer
        // and the IMessageIdTracker mapping.
        return SendTextInternalAsync(chatId, text, correlationId: null, ct);
    }

    /// <inheritdoc />
    public Task<SendResult> SendMessageAsync(long chatId, MessengerMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        // MessengerMessage construction already validates CorrelationId
        // (CorrelationIdValidation.Require), so the value is guaranteed
        // non-null/non-empty/non-whitespace by the time it reaches us.
        return SendTextInternalAsync(chatId, message.Text, message.CorrelationId, ct);
    }

    /// <summary>
    /// Internal split-and-send pipeline used by both the legacy
    /// <see cref="SendTextAsync(long,string,CancellationToken)"/> overload
    /// and the production-path
    /// <see cref="SendMessageAsync(long,MessengerMessage,CancellationToken)"/>
    /// method. Carries an optional <paramref name="correlationId"/> through
    /// to the rendered footer (one footer per chunk) and the
    /// <see cref="IMessageIdTracker"/> mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Escape-then-chunk pipeline (iter-5 structural fix).</b> The raw
    /// body is escaped to MarkdownV2 ONCE up front via
    /// <see cref="MarkdownV2Escaper.Escape"/> and the resulting
    /// already-escaped string is then chunked by
    /// <see cref="SplitEscapedOnBoundaries"/>. This guarantees BOTH:
    /// (a) chunk size budget is enforced against the RENDERED length
    /// Telegram actually sees (so a body dense in MarkdownV2
    /// metacharacters can no longer overflow 4 096 post-escape — the
    /// pre-iter-5 chunk-then-escape pipeline could expand by up to 2× and
    /// blow the limit); (b) the chunker has visibility into escape
    /// pairs and never cuts between a <c>\</c> and the escaped
    /// character (the pre-iter-5 hard-cut path could leave a stray
    /// trailing <c>\</c> in one chunk and the escaped char in the next,
    /// which Telegram rejects as "can't parse entities").
    /// </para>
    /// <para>
    /// Marked <c>internal</c> rather than <c>public</c> so external
    /// callers cannot bypass <see cref="IMessageSender"/> — every
    /// production send must go through one of the interface methods,
    /// which keeps correlation propagation honest. The Tests assembly
    /// has <c>InternalsVisibleTo</c> access for direct exercise of the
    /// chunked-correlation path.
    /// </para>
    /// </remarks>
    internal async Task<SendResult> SendTextInternalAsync(
        long chatId,
        string text,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (correlationId is not null)
        {
            CorrelationIdValidation.Require(correlationId, nameof(correlationId));
        }

        if (text.Length == 0)
        {
            // Telegram rejects empty messages; surface a clear error rather
            // than sending an empty string which would 400 anyway.
            throw new ArgumentException(
                "Cannot send an empty text message via Telegram.",
                nameof(text));
        }

        // Escape ONCE up front, then chunk on the rendered length so the
        // budget reflects what Telegram will actually receive. The
        // chunker also enforces escape-pair integrity (\X stays atomic).
        var escapedBody = MarkdownV2Escaper.Escape(text);
        var footer = correlationId is null ? string.Empty : BuildCorrelationFooter(correlationId);
        var chunks = SplitEscapedOnBoundaries(escapedBody, footer);

        long firstMessageId = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var messageId = await SendSingleAsync(
                chatId,
                chunks[i],
                replyMarkup: null,
                ct).ConfigureAwait(false);
            if (i == 0)
            {
                firstMessageId = messageId;
            }
            if (correlationId is not null)
            {
                // IMessageIdTracker is contractually best-effort — its
                // implementations log + suppress persistence failures
                // rather than throwing so a Telegram-delivered message
                // never gets re-sent on a tracker outage. See
                // IMessageIdTracker XML for the full contract.
                await _tracker.TrackAsync(chatId, messageId, correlationId, ct).ConfigureAwait(false);
            }
        }
        return new SendResult(firstMessageId);
    }

    /// <inheritdoc />
    public async Task<SendResult> SendQuestionAsync(
        long chatId,
        AgentQuestionEnvelope envelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var question = envelope.Question;

        // Validate every callback_data payload up front so any oversize
        // (QuestionId, ActionId) pair fails with a clear ArgumentException
        // at the sender boundary rather than as a cryptic
        // ApiRequestException from Telegram after the cache writes have
        // already happened. Telegram Bot API limits callback_data to
        // 1–64 UTF-8 bytes; see MaxCallbackDataBytes / PR #20 review.
        ValidateCallbackDataLengths(question);

        // Cache full HumanAction payloads BEFORE sending so that an
        // operator who happens to tap a button before our send completes
        // can still resolve the action via CallbackQueryHandler. (The
        // race is extremely narrow but cheap to close.)
        await CacheHumanActionsAsync(question, ct).ConfigureAwait(false);

        var keyboard = BuildInlineKeyboard(question);
        // RenderQuestionBody already escapes its dynamic fields via
        // MarkdownV2Escaper.Escape and emits MarkdownV2-safe template
        // markup; the result is fully escaped MarkdownV2 ready to send.
        // SplitEscapedOnBoundaries enforces escape-pair integrity at
        // hard-cut boundaries so a long body with punctuation cannot
        // produce a chunk that ends with an unpaired '\' (iter-4 item 2).
        var body = RenderQuestionBody(envelope);
        var footer = BuildCorrelationFooter(question.CorrelationId);
        var chunks = SplitEscapedOnBoundaries(body, footer);

        long firstMessageId = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            // Only the first chunk carries the inline keyboard — Telegram
            // does not duplicate a markup across continuation messages,
            // and operators expect the buttons immediately under the
            // question title.
            ReplyMarkup? markup = i == 0 ? keyboard : null;
            var messageId = await SendSingleAsync(chatId, chunks[i], markup, ct)
                .ConfigureAwait(false);
            if (i == 0)
            {
                firstMessageId = messageId;
            }
            // Tracker is contractually best-effort (see IMessageIdTracker
            // XML) so we await it directly; persistence failures are
            // logged and suppressed inside the implementation rather
            // than propagating to cause a duplicate Telegram send.
            await _tracker.TrackAsync(chatId, messageId, question.CorrelationId, ct)
                .ConfigureAwait(false);
        }
        return new SendResult(firstMessageId);
    }

    // ============================================================
    // Internal helpers
    // ============================================================

    private async Task<long> SendSingleAsync(
        long chatId,
        string text,
        ReplyMarkup? replyMarkup,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await _rateLimiter.AcquireAsync(chatId, ct).ConfigureAwait(false);
            try
            {
                return await _api.SendMessageAsync(
                    chatId,
                    text,
                    ParseMode.MarkdownV2,
                    replyMarkup,
                    ct).ConfigureAwait(false);
            }
            catch (ApiRequestException ex) when (IsRateLimited(ex) && attempt < MaxRetryAttempts)
            {
                var retryAfter = TimeSpan.FromSeconds(
                    ex.Parameters?.RetryAfter ?? 1);
                _logger.LogWarning(
                    "Telegram rate-limited send to chat {ChatId} (attempt {Attempt}); backing off for {RetryAfterSeconds}s.",
                    chatId,
                    attempt,
                    retryAfter.TotalSeconds);
                await _delayProvider.DelayAsync(retryAfter, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRateLimited(ApiRequestException ex)
    {
        // Telegram returns HTTP 429 with the error code 429 echoed at the
        // application level. Either signal flags a Too Many Requests.
        if (ex.ErrorCode == 429) return true;
        if (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;
        return false;
    }

    private async Task CacheHumanActionsAsync(AgentQuestion question, CancellationToken ct)
    {
        var absoluteExpiry = question.ExpiresAt + CacheGracePeriod;
        var entryOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = absoluteExpiry,
        };
        foreach (var action in question.AllowedActions)
        {
            var key = BuildCallbackCacheKey(question.QuestionId, action.ActionId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(action, HumanActionSerializerOptions);
            await _cache.SetAsync(key, payload, entryOptions, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cache key shape used by both this sender (write path) and
    /// <c>CallbackQueryHandler</c> (Stage 3.3, read path). Exposed as a
    /// shared static so the two stages cannot drift out of agreement on
    /// the key encoding.
    /// </summary>
    public static string BuildCallbackCacheKey(string questionId, string actionId)
    {
        return $"{questionId}:{actionId}";
    }

    /// <summary>
    /// Builds the Telegram <c>callback_data</c> payload for a question's
    /// inline-keyboard button. Currently the same shape as
    /// <see cref="BuildCallbackCacheKey"/>, but exposed as a distinct
    /// method so the length check and the wire-level encoding stay
    /// co-located.
    /// </summary>
    public static string BuildCallbackData(string questionId, string actionId)
    {
        return $"{questionId}:{actionId}";
    }

    /// <summary>
    /// Validates that every <c>(QuestionId, ActionId)</c> pair fits inside
    /// the Telegram Bot API <c>callback_data</c> limit of
    /// <see cref="MaxCallbackDataBytes"/> UTF-8 bytes. Throws
    /// <see cref="ArgumentException"/> on the first violation so callers
    /// (Stage 4.1 <c>OutboundQueueProcessor</c>, integration tests) get a
    /// diagnostic at the sender boundary rather than an opaque
    /// <see cref="ApiRequestException"/> from Telegram. Per the Bot API
    /// docs (and PR #20 review): two 36-char GUIDs joined by ':' total
    /// 73 bytes, which Telegram rejects.
    /// </summary>
    /// <remarks>
    /// Defense-in-depth: <see cref="AgentQuestion.QuestionId"/> and
    /// <see cref="HumanAction.ActionId"/> both enforce a 30-ASCII-char
    /// construction-time cap, so a well-formed envelope cannot reach
    /// this method with an oversize payload. This sender-side guard
    /// remains in place because (a) the wire-level Telegram constraint
    /// is byte-oriented and could be re-defined upstream without
    /// touching the abstractions, (b) any future loosening of the
    /// per-field caps (e.g., non-ASCII labels, longer IDs) would
    /// silently regress on the wire if only the per-field guards
    /// existed, and (c) failing fast with a sender-specific message
    /// (<c>"Telegram callback_data must be 1–64 UTF-8 bytes..."</c>)
    /// keeps the diagnostic close to the call site.
    /// </remarks>
    internal static void ValidateCallbackDataLengths(AgentQuestion question)
    {
        foreach (var action in question.AllowedActions)
        {
            ValidateCallbackDataLength(question.QuestionId, action.ActionId);
        }
    }

    /// <summary>
    /// Validates a single <c>(questionId, actionId)</c> pair against the
    /// Telegram 1–64 UTF-8 byte <c>callback_data</c> limit. Extracted
    /// from <see cref="ValidateCallbackDataLengths(AgentQuestion)"/> so
    /// the wire-level check can be exercised in isolation by unit tests
    /// without having to bypass the record-level construction guards in
    /// <see cref="AgentQuestion"/> / <see cref="HumanAction"/>.
    /// </summary>
    internal static void ValidateCallbackDataLength(string questionId, string actionId)
    {
        var payload = BuildCallbackData(questionId, actionId);
        var byteLength = Encoding.UTF8.GetByteCount(payload);
        if (byteLength == 0 || byteLength > MaxCallbackDataBytes)
        {
            throw new ArgumentException(
                $"Telegram callback_data must be 1–{MaxCallbackDataBytes} UTF-8 bytes; "
                + $"composed payload '{payload}' is {byteLength} bytes "
                + $"(QuestionId='{questionId}', ActionId='{actionId}'). "
                + "Shorten the QuestionId or ActionId — Telegram will reject sendMessage otherwise.");
        }
    }

    private static InlineKeyboardMarkup BuildInlineKeyboard(AgentQuestion question)
    {
        var rows = new List<InlineKeyboardButton[]>(question.AllowedActions.Count);
        foreach (var action in question.AllowedActions)
        {
            var label = action.RequiresComment
                ? $"{action.Label} (reply required)"
                : action.Label;
            var callbackData = BuildCallbackData(question.QuestionId, action.ActionId);
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(label, callbackData) });
        }
        return new InlineKeyboardMarkup(rows);
    }

    private string RenderQuestionBody(AgentQuestionEnvelope envelope)
    {
        var question = envelope.Question;
        var sb = new StringBuilder();

        var icon = SeverityIcon(question.Severity);
        sb.Append(icon)
          .Append(" *\\[")
          .Append(MarkdownV2Escaper.Escape(question.Severity.ToString()))
          .Append("\\]* ")
          .AppendLine(MarkdownV2Escaper.Escape(question.Title));

        sb.AppendLine();
        sb.AppendLine(MarkdownV2Escaper.Escape(question.Body));

        if (envelope.ProposedDefaultActionId is not null)
        {
            // ----------------------------------------------------------
            // Stage 2.3 ↔ Stage 3.5/4.1 boundary (rendering vs. denormalization):
            //
            // The Stage 2.3 sender RENDERS the proposed default action
            // label into the operator-facing body so the human sees
            // "Default action if no response: Approve". Denormalising
            // the ActionId into the Stage 3.5 record's default-action
            // field is OUT OF SCOPE here — it belongs to the Stage 4.1
            // OutboundQueueProcessor's post-send hook, which writes
            // through the Stage 3.5 store contract. See IMessageSender's
            // docstring and implementation-plan.md Stage 1.4 for the
            // formal responsibility split. This boundary is pinned by
            // a constructor-shape reflection assertion in the Tests
            // assembly.
            // ----------------------------------------------------------
            var defaultAction = question.AllowedActions.FirstOrDefault(
                a => string.Equals(a.ActionId, envelope.ProposedDefaultActionId, StringComparison.Ordinal));
            if (defaultAction is not null)
            {
                sb.AppendLine();
                sb.Append("Default action if no response: ")
                  .AppendLine(MarkdownV2Escaper.Escape(defaultAction.Label));
            }
        }

        var now = _timeProvider.GetUtcNow();
        var remaining = question.ExpiresAt - now;
        sb.AppendLine();
        if (remaining > TimeSpan.Zero)
        {
            sb.Append("⏰ Expires in ")
              .Append(MarkdownV2Escaper.Escape(HumanizeRelative(remaining)))
              .AppendLine();
        }
        else
        {
            sb.Append("⏰ Expired at ")
              .Append(MarkdownV2Escaper.Escape(question.ExpiresAt.UtcDateTime.ToString("u")))
              .AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string SeverityIcon(MessageSeverity severity) => severity switch
    {
        MessageSeverity.Critical => "🚨",
        MessageSeverity.High => "⚠️",
        MessageSeverity.Normal => "ℹ️",
        MessageSeverity.Low => "📝",
        _ => "ℹ️",
    };

    private static string HumanizeRelative(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return FormattableString.Invariant($"{(int)span.TotalDays} days");
        }
        if (span.TotalHours >= 1)
        {
            return FormattableString.Invariant($"{(int)span.TotalHours} hours");
        }
        if (span.TotalMinutes >= 1)
        {
            return FormattableString.Invariant($"{(int)span.TotalMinutes} minutes");
        }
        return FormattableString.Invariant($"{Math.Max(1, (int)span.TotalSeconds)} seconds");
    }

    private static string BuildCorrelationFooter(string correlationId)
    {
        // MarkdownV2 footer; the literal "Trace:" label is verbatim ASCII
        // and the correlation id is escaped for safety. The leading two
        // newlines visually separate the footer from the body when
        // rendered in a Telegram chat.
        return "\n\nTrace: " + MarkdownV2Escaper.Escape(correlationId);
    }

    /// <summary>
    /// Split an already-MarkdownV2-escaped body into one or more chunks
    /// no larger than the Telegram 4 096-character message limit,
    /// appending <paramref name="footer"/> to each chunk so every
    /// physical message carries the trace id (per e2e-scenarios.md
    /// "Long message split into chunks"). Boundary preference is
    /// paragraph (<c>\n\n</c>) → line (<c>\n</c>) → word (<c> </c>) →
    /// hard cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inputs are pre-escaped (iter-5 structural fix).</b> Both
    /// callers (<see cref="SendTextInternalAsync"/> via
    /// <see cref="MarkdownV2Escaper.Escape"/>; <see cref="SendQuestionAsync"/>
    /// via <see cref="RenderQuestionBody"/>) hand this method a
    /// fully-escaped body so the budget check matches what Telegram
    /// actually receives. The pre-iter-5 dual chunker
    /// (<c>BuildRawChunks</c> + <c>BuildPreEscapedChunks</c>) had a
    /// pre-escape budget for plain text that could overflow 4 096 by up
    /// to 2× when the input was dense in MarkdownV2 metacharacters
    /// (e.g. 5 000 <c>.</c> chars escape to 10 000 <c>\.</c> chars).
    /// </para>
    /// <para>
    /// <b>Escape-pair integrity (iter-5 structural fix).</b>
    /// <see cref="MarkdownV2Escaper.Escape"/>'s output is a
    /// concatenation of 2-char escape tokens (<c>\X</c> where <c>X</c>
    /// is one of the reserved characters, including the literal
    /// backslash encoded as <c>\\</c>) and ordinary non-backslash
    /// literals. Equivalently: every non-token <c>\</c>-run in the
    /// output has even length. Cutting a chunk inside a
    /// token would leave one chunk ending in a stray <c>\</c> and the
    /// next chunk starting with the escaped char — Telegram rejects
    /// both as <c>can't parse entities</c>. The cut position chosen by
    /// boundary search or hard-cut is therefore passed through
    /// <see cref="AdjustForEscapePair"/>: when the count of consecutive
    /// trailing backslashes ending at <paramref name="cutPos"/>-1 is
    /// odd, the cut is inside a token and is backed off by 1 so the
    /// token stays in the next chunk. When the count is even, the cut
    /// lies on a token boundary and the original position is kept.
    /// </para>
    /// <para>
    /// <b>Boundary cuts are always escape-safe.</b> The escaper never
    /// emits <c>\&lt;space&gt;</c>, <c>\&lt;newline&gt;</c> or
    /// <c>\&lt;CR&gt;</c> because none of those characters are in the
    /// MarkdownV2 reserved set. So a paragraph / line / space boundary
    /// match is always preceded by a non-backslash character —
    /// <see cref="AdjustForEscapePair"/> is a no-op on those paths.
    /// The adjustment matters only on the hard-cut path.
    /// </para>
    /// <para>
    /// <b>Tiny-budget guard.</b> The smallest valid chunk after
    /// splitting must contain at least one full escape token = 2 chars
    /// (<c>\X</c>); if the footer leaves <c>limit &lt; 2</c> the
    /// chunker throws rather than producing zero-progress iterations.
    /// In production the footer is bounded by the
    /// <see cref="MessagingDbContext"/> 256-char correlation-id
    /// constraint, so <c>limit ≥ 4 096 - (9 + 2·256) = 3 575</c>; the
    /// guard is purely defensive against future changes to the
    /// correlation-id contract.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> SplitEscapedOnBoundaries(string escapedBody, string footer)
    {
        if (string.IsNullOrEmpty(escapedBody))
        {
            return Array.Empty<string>();
        }

        // Single-chunk fast path: no need to enforce the per-chunk
        // tiny-budget guard when no splitting will occur.
        if (escapedBody.Length + footer.Length <= MaxTelegramMessageLength)
        {
            return new[] { escapedBody + footer };
        }

        var limit = MaxTelegramMessageLength - footer.Length;
        if (limit < 2)
        {
            throw new InvalidOperationException(
                $"Correlation footer leaves only {limit} char(s) per chunk; need at least 2 to fit a single MarkdownV2 escape token (\\X). Reduce footer / correlation-id length.");
        }

        var chunks = new List<string>();
        var pos = 0;
        while (pos < escapedBody.Length)
        {
            var remaining = escapedBody.Length - pos;
            if (remaining <= limit)
            {
                chunks.Add(escapedBody.Substring(pos) + footer);
                break;
            }

            var windowEnd = pos + limit;
            var split = LastIndexOfWithin(escapedBody, "\n\n", pos, limit);
            if (split <= pos)
            {
                split = LastIndexOfWithin(escapedBody, "\n", pos, limit);
            }
            if (split <= pos)
            {
                split = LastIndexOfWithin(escapedBody, " ", pos, limit);
            }
            if (split <= pos)
            {
                split = windowEnd;
            }

            // Avoid cutting inside a \X escape token. With limit ≥ 2
            // (enforced above) the back-off can step at most 1 char
            // before windowEnd, so split is always > pos here:
            //   - boundary cuts land after a non-backslash separator
            //     (escaper never emits \<space>, \<newline>, \<CR>) so
            //     adjustment is a no-op;
            //   - the hard-cut path may back off by 1, but
            //     windowEnd - 1 = pos + limit - 1 ≥ pos + 1 > pos.
            split = AdjustForEscapePair(escapedBody, split);

            var slice = escapedBody.Substring(pos, split - pos).TrimEnd();
            chunks.Add(slice + footer);
            pos = split;
            // Eat trailing whitespace so the next chunk starts on a real
            // character boundary instead of carrying the separator.
            while (pos < escapedBody.Length &&
                   (escapedBody[pos] == '\n' || escapedBody[pos] == ' ' || escapedBody[pos] == '\r'))
            {
                pos++;
            }
        }
        return chunks;
    }

    /// <summary>
    /// Adjust <paramref name="cutPos"/> backward so it never lands
    /// inside a 2-char MarkdownV2 escape token (<c>\X</c>). Counts
    /// consecutive trailing backslashes ending at
    /// <paramref name="cutPos"/>-1: if the count is odd, the cut would
    /// split the last token, so back off by 1 to keep the token whole.
    /// </summary>
    /// <remarks>
    /// Sound for any output produced by
    /// <see cref="MarkdownV2Escaper.Escape"/> or by
    /// <see cref="RenderQuestionBody"/>'s template. Both producers
    /// emit a sequence of 2-char <c>\X</c> tokens plus ordinary
    /// non-backslash literals; runs of backslashes always have even
    /// length (each literal <c>\</c> in the input becomes the 2-char
    /// token <c>\\</c> in the output). Counting parity therefore
    /// correctly identifies a mid-token cut.
    /// </remarks>
    internal static int AdjustForEscapePair(string escapedText, int cutPos)
    {
        if (cutPos <= 0 || cutPos > escapedText.Length)
        {
            return cutPos;
        }
        var backslashes = 0;
        var i = cutPos - 1;
        while (i >= 0 && escapedText[i] == '\\')
        {
            backslashes++;
            i--;
        }
        return (backslashes % 2 == 1) ? cutPos - 1 : cutPos;
    }

    private static int LastIndexOfWithin(string s, string needle, int start, int length)
    {
        // Search within s[start .. start+length) for the last occurrence
        // of needle, returning the absolute index immediately AFTER the
        // matched separator (so the caller can use it as the next slice
        // start position) — or -1 when not found.
        var searchEnd = start + length - needle.Length;
        if (searchEnd < start) return -1;
        for (var i = searchEnd; i >= start; i--)
        {
            if (i + needle.Length > s.Length) continue;
            var matches = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (s[i + j] != needle[j])
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                return i + needle.Length;
            }
        }
        return -1;
    }
}
