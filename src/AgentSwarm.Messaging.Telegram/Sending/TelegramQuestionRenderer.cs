using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Builds the MarkdownV2 body and <see cref="InlineKeyboardMarkup"/>
/// for an outbound agent question, and writes each
/// <see cref="HumanAction"/> to <see cref="IDistributedCache"/> keyed by
/// <c>QuestionId:ActionId</c> so the Stage 3.3
/// <c>CallbackQueryHandler</c> can resolve the full action from a short
/// callback payload at button-tap time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rendering boundary (architecture.md §4.12 / §5.2 invariant 1).</b>
/// <see cref="TelegramMessageSender"/> is the sole owner of question
/// rendering. The renderer extracts the <see cref="AgentQuestion"/>
/// from the envelope, surfaces the proposed default action's label in
/// the body, includes severity badge / timeout / correlation id, and
/// renders <see cref="AgentQuestion.AllowedActions"/> as inline buttons
/// with callback data encoded as <c>QuestionId:ActionId</c>.
/// </para>
/// <para>
/// <b>Cache write semantics (architecture.md §5.2 invariant 2 +
/// implementation-plan Stage 2.3).</b> One cache entry is written per
/// action with expiry set to <see cref="AgentQuestion.ExpiresAt"/> +
/// <see cref="CacheGracePeriod"/> (the 5-minute grace window ensures
/// the callback handler can still resolve late button taps near the
/// expires boundary). The cache is populated <i>before</i> the Telegram
/// API call so a callback that arrives before the send returns can
/// still resolve.
/// </para>
/// <para>
/// <b>Default-action display vs. denormalisation.</b> The renderer
/// reads <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>
/// purely for display ("Default action if no response: …"). It does
/// NOT itself persist <c>PendingQuestionRecord.DefaultActionId</c> —
/// per Stage 3.5, the enclosing <c>TelegramMessageSender</c> calls
/// <see cref="IPendingQuestionStore.StoreAsync"/> directly after a
/// successful Telegram send, and the store denormalises
/// <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/> into
/// both <c>PendingQuestionRecord.DefaultActionId</c> (primary timeout
/// source) and <c>PendingQuestionRecord.DefaultActionValue</c>
/// (callback / RequiresComment text-reply fallback) at persistence
/// time (architecture.md §5.2 invariant 1).
/// </para>
/// </remarks>
internal static class TelegramQuestionRenderer
{
    /// <summary>
    /// Grace window appended to <see cref="AgentQuestion.ExpiresAt"/>
    /// when scheduling cache eviction. Aligned with implementation-plan
    /// Stage 2.3 and architecture.md §5.2 — keeps late button taps
    /// resolvable while the question is technically expired.
    /// </summary>
    public static readonly TimeSpan CacheGracePeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Label suffix appended to buttons whose backing action has
    /// <see cref="HumanAction.RequiresComment"/> = <c>true</c> so the
    /// operator knows a follow-up text reply is expected after tapping.
    /// </summary>
    public const string RequiresCommentSuffix = " (reply required)";

    /// <summary>
    /// Builds the callback-data payload — <c>QuestionId:ActionId</c> —
    /// for one button. Both component identifiers are validated at
    /// construction time on the abstraction records (see
    /// <see cref="AgentQuestion.QuestionId"/> and
    /// <see cref="HumanAction.ActionId"/>) so the joined payload is
    /// provably ≤ 61 UTF-8 bytes, within Telegram's 64-byte budget.
    /// </summary>
    public static string BuildCallbackData(string questionId, string actionId) =>
        questionId + ":" + actionId;

    /// <summary>
    /// Builds the MarkdownV2 body for the supplied
    /// <paramref name="envelope"/>. Includes severity badge, timeout
    /// countdown, full question body, optional proposed-default-action
    /// label, and the trace-id footer mandated by
    /// architecture.md §10.1.
    /// </summary>
    public static string BuildBody(AgentQuestionEnvelope envelope, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var q = envelope.Question;
        var now = timeProvider.GetUtcNow();
        var timeout = q.ExpiresAt - now;

        var builder = new StringBuilder();
        builder.Append('*').Append(MarkdownV2.Escape(q.Title)).Append('*').Append('\n');
        builder.Append('\n');

        builder.Append(SeverityBadge(q.Severity))
               .Append(' ')
               .Append(MarkdownV2.Escape("Severity: " + q.Severity))
               .Append('\n');

        builder.Append(MarkdownV2.Escape("Times out " + FormatTimeout(timeout)
                       + " (at " + q.ExpiresAt.ToString("u") + ")"))
               .Append('\n');

        if (envelope.ProposedDefaultActionId is { } defaultId)
        {
            var defaultLabel = ResolveDefaultActionLabel(q, defaultId);
            builder.Append(MarkdownV2.Escape("Default action if no response: " + defaultLabel))
                   .Append('\n');
        }

        builder.Append('\n');
        builder.Append(MarkdownV2.Escape(q.Body));
        builder.Append('\n');

        // Trace footer — architecture.md §10.1 "Renderer invariant
        // (outbound)": every rendered message ends with the
        // correlation id so operators / audit reviewers can map any
        // Telegram message back to its end-to-end trace.
        builder.Append('\n');
        builder.Append(MarkdownV2.Escape("🔗 trace: " + q.CorrelationId));

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <see cref="InlineKeyboardMarkup"/> for the supplied
    /// <paramref name="question"/>. Each <see cref="HumanAction"/> is
    /// rendered as a single button on its own row, with
    /// <c>callback_data</c> = <c>QuestionId:ActionId</c> and the
    /// "(reply required)" suffix added when
    /// <see cref="HumanAction.RequiresComment"/> is <c>true</c>.
    /// </summary>
    public static InlineKeyboardMarkup BuildInlineKeyboard(AgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        var rows = new List<InlineKeyboardButton[]>(question.AllowedActions.Count);
        foreach (var action in question.AllowedActions)
        {
            var label = action.RequiresComment
                ? action.Label + RequiresCommentSuffix
                : action.Label;
            var button = InlineKeyboardButton.WithCallbackData(
                text: label,
                callbackData: BuildCallbackData(question.QuestionId, action.ActionId));
            rows.Add(new[] { button });
        }

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Persist each <see cref="HumanAction"/> in
    /// <paramref name="question"/> into <paramref name="cache"/>, keyed
    /// by <c>QuestionId:ActionId</c>, with absolute expiry set to
    /// <see cref="AgentQuestion.ExpiresAt"/> +
    /// <see cref="CacheGracePeriod"/>. Stage 3.3
    /// <c>CallbackQueryHandler</c> reads these entries on inline-button
    /// callbacks to resolve the full action without consulting the
    /// durable <c>PendingQuestionRecord</c> store on the hot callback
    /// path.
    /// </summary>
    public static async Task CacheActionsAsync(
        AgentQuestion question,
        IDistributedCache cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var now = timeProvider.GetUtcNow();
        var ttl = (question.ExpiresAt - now) + CacheGracePeriod;
        // Guard: if ExpiresAt is already in the past, the absolute
        // expiry would also be in the past and IDistributedCache would
        // reject the entry. Use the grace window as the relative TTL
        // floor so a stale-but-just-arriving question still caches
        // long enough for any in-flight late-callback to resolve.
        if (ttl <= TimeSpan.Zero)
        {
            ttl = CacheGracePeriod;
        }

        // Note: we use AbsoluteExpirationRelativeToNow rather than
        // AbsoluteExpiration because IDistributedCache implementations
        // anchor absolute expiries to their own wall-clock (UtcNow),
        // not to our injected TimeProvider. A test that uses a
        // FakeTimeProvider with a non-current GetUtcNow() would
        // otherwise produce an entry that the cache instantly considers
        // expired. The relative form sidesteps that mismatch — the
        // cache adds the TTL to its own UtcNow, which is exactly the
        // semantics architecture.md §5.2 asks for.
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
        };

        foreach (var action in question.AllowedActions)
        {
            var key = BuildCallbackData(question.QuestionId, action.ActionId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(action);
            await cache.SetAsync(key, payload, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveDefaultActionLabel(AgentQuestion question, string defaultActionId)
    {
        foreach (var action in question.AllowedActions)
        {
            if (string.Equals(action.ActionId, defaultActionId, StringComparison.Ordinal))
            {
                return action.Label;
            }
        }

        // Envelope construction validates that ProposedDefaultActionId
        // matches one of AllowedActions, so this branch is unreachable
        // for any envelope that survived the abstraction's invariants.
        // Falling back to the id itself avoids a NullReferenceException
        // in the unlikely scenario where the envelope was constructed
        // through reflection / a future bypass path.
        return defaultActionId;
    }

    private static string SeverityBadge(MessageSeverity severity) => severity switch
    {
        MessageSeverity.Critical => "🚨",
        MessageSeverity.High => "⚠️",
        MessageSeverity.Normal => "ℹ️",
        MessageSeverity.Low => "•",
        _ => "•",
    };

    private static string FormatTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return "now";
        }

        if (timeout.TotalMinutes < 1)
        {
            return "in " + (int)timeout.TotalSeconds + " s";
        }
        if (timeout.TotalHours < 1)
        {
            return "in " + (int)timeout.TotalMinutes + " min";
        }

        return "in " + ((int)timeout.TotalHours).ToString() + " h "
             + (timeout.Minutes).ToString() + " min";
    }
}
