using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Bot Framework <see cref="IMiddleware"/> stage that suppresses duplicate inbound webhook
/// deliveries. Looks up the inbound <see cref="Microsoft.Bot.Schema.Activity.Id"/> (or
/// <see cref="Microsoft.Bot.Schema.Activity.ReplyToId"/> for invoke activities) against the
/// injected <see cref="IActivityIdStore"/> and short-circuits subsequent middleware /
/// handler invocations when a duplicate is detected.
/// </summary>
/// <remarks>
/// <para>
/// Aligned with <c>architecture.md</c> §2.16 and Stage 2.1 implementation plan step 8. The
/// store's TTL defaults to <c>TeamsMessagingOptions.DeduplicationTtlMinutes</c> (10
/// minutes). This middleware is distinct from Stage 6.2's domain-level idempotency check
/// which operates on <c>(QuestionId, UserId)</c> pairs.
/// </para>
/// </remarks>
public sealed class ActivityDeduplicationMiddleware : IMiddleware
{
    private readonly IActivityIdStore _store;
    private readonly ILogger<ActivityDeduplicationMiddleware> _logger;

    /// <summary>
    /// Initialize a new <see cref="ActivityDeduplicationMiddleware"/>.
    /// </summary>
    /// <param name="store">The deduplication state store.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public ActivityDeduplicationMiddleware(
        IActivityIdStore store,
        ILogger<ActivityDeduplicationMiddleware> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken = default)
    {
        if (turnContext is null) throw new ArgumentNullException(nameof(turnContext));
        if (next is null) throw new ArgumentNullException(nameof(next));

        var activity = turnContext.Activity;
        // Per architecture.md §2.16: invoke activities carry the user's interaction with a
        // previously-sent card, so ReplyToId identifies that card uniquely and is the right
        // dedup key. For all other activity types (Message, ConversationUpdate, etc.) two
        // distinct activities may legitimately share the same ReplyToId (e.g., two replies in
        // the same thread), so dedup is keyed off Activity.Id.
        var dedupKey = string.Equals(activity?.Type, Microsoft.Bot.Schema.ActivityTypes.Invoke, StringComparison.OrdinalIgnoreCase)
                && activity?.ReplyToId is { Length: > 0 } replyTo
            ? replyTo
            : activity?.Id;

        if (string.IsNullOrEmpty(dedupKey))
        {
            // Nothing to deduplicate on — pass through.
            await next(cancellationToken).ConfigureAwait(false);
            return;
        }

        var alreadySeen = await _store.IsSeenOrMarkAsync(dedupKey, cancellationToken).ConfigureAwait(false);
        if (alreadySeen)
        {
            _logger.LogInformation(
                "ActivityDeduplicationMiddleware: suppressed duplicate activity {ActivityId} (type={ActivityType}).",
                dedupKey,
                activity?.Type);
            return;
        }

        await next(cancellationToken).ConfigureAwait(false);
    }
}
