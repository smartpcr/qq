using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Stage-2.1 <see cref="ICommandDispatcher"/> stub. Replies with a fixed
/// "commands not yet available" message so the activity-handler can be wired before the
/// concrete <c>CommandDispatcher</c> ships in Stage 3.2.
/// </summary>
public sealed class NoOpCommandDispatcher : ICommandDispatcher
{
    private readonly ILogger<NoOpCommandDispatcher> _logger;

    /// <summary>Initialize a new <see cref="NoOpCommandDispatcher"/>.</summary>
    public NoOpCommandDispatcher(ILogger<NoOpCommandDispatcher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(CommandContext context, CancellationToken ct)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        _logger.LogInformation(
            "NoOpCommandDispatcher invoked for normalized text '{NormalizedText}'. " +
            "Replace with concrete CommandDispatcher in Stage 3.2.",
            context.NormalizedText);

        if (context.TurnContext is ITurnContext turnContext)
        {
            await turnContext
                .SendActivityAsync("Commands are not yet available — placeholder dispatcher.", cancellationToken: ct)
                .ConfigureAwait(false);
        }
    }
}
