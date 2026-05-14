using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Stub <see cref="ICommandDispatcher"/> registered in Stage 2.1 until the concrete
/// <c>CommandDispatcher</c> lands in Stage 3.2. Replies to the user with a polite "commands
/// not yet available" message so the bot is responsive end-to-end while the parser is being
/// implemented.
/// </summary>
/// <remarks>
/// The stub reads <see cref="CommandContext.TurnContext"/> back to a Bot Framework
/// <see cref="ITurnContext"/> when available so it can post the reply through the active
/// conversation. When the turn context is unavailable (for example, during unit-test
/// invocations) it simply logs and returns.
/// </remarks>
public sealed class NoOpCommandDispatcher : ICommandDispatcher
{
    private const string ReplyText = "Commands are not yet available — the production dispatcher ships in Stage 3.2.";
    private readonly ILogger<NoOpCommandDispatcher> _logger;

    /// <summary>
    /// Initialize a new <see cref="NoOpCommandDispatcher"/>.
    /// </summary>
    /// <param name="logger">Logger used for diagnostic output.</param>
    public NoOpCommandDispatcher(ILogger<NoOpCommandDispatcher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(CommandContext context, CancellationToken ct)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "NoOpCommandDispatcher received command text '{NormalizedText}' (CorrelationId={CorrelationId}). " +
            "Replace with CommandDispatcher (Stage 3.2) for live behavior.",
            context.NormalizedText,
            context.CorrelationId);

        if (context.TurnContext is ITurnContext turnContext)
        {
            await turnContext
                .SendActivityAsync(MessageFactory.Text(ReplyText), ct)
                .ConfigureAwait(false);
        }
    }
}
