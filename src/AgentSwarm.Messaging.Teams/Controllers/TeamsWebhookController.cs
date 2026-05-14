using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace AgentSwarm.Messaging.Teams.Controllers;

/// <summary>
/// HTTP entry point for the Teams Bot Framework channel. POST <c>/api/messages</c> activities
/// are delegated to <see cref="IBotFrameworkHttpAdapter.ProcessAsync"/>, which performs JWT
/// validation and dispatches the activity through the Bot Framework middleware pipeline.
/// </summary>
/// <remarks>
/// Located in <c>AgentSwarm.Messaging.Teams</c> per <c>architecture.md</c> §2.2 / §7. The
/// hosting <c>AgentSwarm.Messaging.Worker</c> project discovers the controller via
/// <c>AddApplicationPart(typeof(TeamsWebhookController).Assembly)</c>.
/// </remarks>
[ApiController]
[Route("api/messages")]
public sealed class TeamsWebhookController : ControllerBase
{
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IBot _bot;

    /// <summary>Initialize a new <see cref="TeamsWebhookController"/>.</summary>
    public TeamsWebhookController(IBotFrameworkHttpAdapter adapter, IBot bot)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
    }

    /// <summary>POST <c>/api/messages</c> entry point — delegate to the bot adapter.</summary>
    [HttpPost]
    public Task PostAsync(CancellationToken ct)
        => _adapter.ProcessAsync(Request, Response, _bot, ct);
}
