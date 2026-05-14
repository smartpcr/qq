using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Bot Framework <see cref="IMiddleware"/> that suppresses duplicate inbound webhook
/// deliveries by checking <c>Activity.Id</c> (or <c>Activity.ReplyToId</c> for invoke
/// activities) against an injected <see cref="IActivityIdStore"/>. Duplicates short-circuit
/// the pipeline before reaching the bot handler.
/// </summary>
/// <remarks>
/// This is distinct from the domain-level idempotency check performed by
/// <c>CardActionHandler</c> (Stage 6.2) which operates on <c>(QuestionId, UserId)</c>
/// pairs — this middleware handles transport-level retry duplicates from Teams.
/// </remarks>
public sealed class ActivityDeduplicationMiddleware : IMiddleware
{
    private readonly IActivityIdStore _store;
    private readonly ILogger<ActivityDeduplicationMiddleware> _logger;

    /// <summary>Initialize a new <see cref="ActivityDeduplicationMiddleware"/>.</summary>
    public ActivityDeduplicationMiddleware(
        IActivityIdStore store,
        ILogger<ActivityDeduplicationMiddleware> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnTurnAsync(
        ITurnContext turnContext,
        NextDelegate next,
        CancellationToken cancellationToken = default)
    {
        if (turnContext is null) throw new ArgumentNullException(nameof(turnContext));
        if (next is null) throw new ArgumentNullException(nameof(next));

        var activity = turnContext.Activity;
        var key = !string.IsNullOrWhiteSpace(activity?.ReplyToId)
            ? activity!.ReplyToId
            : activity?.Id;

        if (string.IsNullOrWhiteSpace(key))
        {
            // Without an activity ID we cannot dedup — fall through to the handler.
            await next(cancellationToken).ConfigureAwait(false);
            return;
        }

        var seen = await _store.IsSeenOrMarkAsync(key, cancellationToken).ConfigureAwait(false);
        if (seen)
        {
            _logger.LogInformation(
                "Duplicate inbound activity suppressed by ActivityDeduplicationMiddleware (key={ActivityKey}, type={ActivityType})",
                key,
                activity?.Type);
            return;
        }

        await next(cancellationToken).ConfigureAwait(false);
    }
}
