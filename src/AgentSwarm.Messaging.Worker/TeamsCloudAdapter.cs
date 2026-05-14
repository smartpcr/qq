using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;

namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// Bot Framework <see cref="CloudAdapter"/> configured with the Stage 2.1 Bot Framework
/// middleware pipeline: <see cref="TelemetryMiddleware"/> → <see cref="ActivityDeduplicationMiddleware"/>.
/// </summary>
/// <remarks>
/// The HTTP-layer middleware (<c>TenantValidationMiddleware</c>, <c>RateLimitMiddleware</c>)
/// runs BEFORE this adapter inside the ASP.NET Core pipeline and is NOT registered here.
/// </remarks>
public sealed class TeamsCloudAdapter : CloudAdapter
{
    /// <summary>Construct a new <see cref="TeamsCloudAdapter"/>.</summary>
    public TeamsCloudAdapter(
        BotFrameworkAuthentication botFrameworkAuthentication,
        ILogger<TeamsCloudAdapter> logger,
        TelemetryMiddleware telemetryMiddleware,
        ActivityDeduplicationMiddleware deduplicationMiddleware)
        : base(botFrameworkAuthentication, logger)
    {
        if (telemetryMiddleware is null) throw new ArgumentNullException(nameof(telemetryMiddleware));
        if (deduplicationMiddleware is null) throw new ArgumentNullException(nameof(deduplicationMiddleware));

        Use(telemetryMiddleware);
        Use(deduplicationMiddleware);

        OnTurnError = async (turnContext, exception) =>
        {
            logger.LogError(exception, "Unhandled exception in TeamsCloudAdapter turn pipeline.");
            await turnContext.SendActivityAsync("The bot encountered an error.").ConfigureAwait(false);
        };
    }
}
