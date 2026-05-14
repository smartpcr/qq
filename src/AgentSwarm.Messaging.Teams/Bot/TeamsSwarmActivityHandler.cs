using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Bot;

/// <summary>
/// Stage-2.1 placeholder <see cref="TeamsActivityHandler"/>. The full handler with command
/// dispatch, identity resolution, and audit-log integration lands in Stage 2.2. This stub
/// exists so the bot adapter (<c>CloudAdapter</c>) has an <see cref="IBot"/> binding and the
/// pipeline can be exercised end-to-end via the controller and middleware.
/// </summary>
public sealed class TeamsSwarmActivityHandler : TeamsActivityHandler
{
    private readonly ILogger<TeamsSwarmActivityHandler> _logger;

    /// <summary>Initialize a new <see cref="TeamsSwarmActivityHandler"/>.</summary>
    public TeamsSwarmActivityHandler(ILogger<TeamsSwarmActivityHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default)
    {
        if (turnContext is null) throw new ArgumentNullException(nameof(turnContext));
        _logger.LogDebug(
            "TeamsSwarmActivityHandler turn: type={ActivityType} id={ActivityId} channel={ChannelId}",
            turnContext.Activity?.Type,
            turnContext.Activity?.Id,
            turnContext.Activity?.ChannelId);
        return base.OnTurnAsync(turnContext, cancellationToken);
    }
}
