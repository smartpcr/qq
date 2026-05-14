using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace AgentSwarm.Messaging.Teams.Controllers;

/// <summary>
/// ASP.NET Core controller exposing the Bot Framework webhook at <c>POST /api/messages</c>.
/// Aligned with <c>architecture.md</c> §2.2 (TeamsWebhookController) and Stage 2.1 step 15.
/// </summary>
/// <remarks>
/// <para>
/// Delegates all processing to <see cref="IBotFrameworkHttpAdapter.ProcessAsync"/>, which
/// runs the configured <see cref="CloudAdapter"/> pipeline (JWT validation → Bot Framework
/// middleware → <see cref="IBot"/>). The Worker project's <c>Program.cs</c> discovers this
/// controller at runtime via
/// <c>builder.Services.AddControllers().AddApplicationPart(typeof(TeamsWebhookController).Assembly)</c>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/messages")]
public sealed class TeamsWebhookController : ControllerBase
{
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IBot _bot;

    /// <summary>
    /// Initialize a new <see cref="TeamsWebhookController"/>.
    /// </summary>
    /// <param name="adapter">The Bot Framework HTTP adapter (resolves to <see cref="CloudAdapter"/>).</param>
    /// <param name="bot">The registered bot implementation (resolves to <c>TeamsSwarmActivityHandler</c>).</param>
    public TeamsWebhookController(IBotFrameworkHttpAdapter adapter, IBot bot)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
    }

    /// <summary>
    /// Receive an inbound Bot Framework activity.
    /// </summary>
    /// <returns>An asynchronous task that completes once the adapter has handled the request.</returns>
    [HttpPost]
    public async Task PostAsync()
    {
        await _adapter.ProcessAsync(Request, Response, _bot, HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
