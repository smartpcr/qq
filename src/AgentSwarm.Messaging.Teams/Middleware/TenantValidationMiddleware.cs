using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// ASP.NET Core HTTP middleware that enforces the per-tenant allow-list before any Bot
/// Framework code runs. Aligned with <c>architecture.md</c> §5.4 and the Stage 2.1
/// implementation plan.
/// </summary>
/// <remarks>
/// <para>
/// Runs early in the ASP.NET Core pipeline so it can short-circuit the request with HTTP 403
/// before <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/>
/// receives the payload (and therefore before JWT authentication or any Bot Framework
/// <see cref="Microsoft.Bot.Builder.IMiddleware"/> stage executes). The pipeline order is:
/// <c>TenantValidationMiddleware</c> (HTTP 403) → <c>RateLimitMiddleware</c> (HTTP 429) →
/// <c>CloudAdapter.ProcessAsync</c> (JWT 401 + Bot Framework middleware) →
/// <c>TeamsSwarmActivityHandler</c>.
/// </para>
/// <para>
/// Only requests to the bot's webhook endpoint (<c>/api/messages</c>) are inspected; other
/// endpoints (health probes, metrics) bypass the check entirely. The middleware buffers the
/// request body with <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/>
/// before reading, so <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/>
/// can re-read the same payload downstream.
/// </para>
/// <para>
/// Audit logging at this stage is limited to <see cref="ILogger"/> warnings; Stage 5.1
/// upgrades this same middleware to emit a <c>SecurityRejection</c> audit entry via
/// <c>IAuditLogger</c>.
/// </para>
/// </remarks>
public sealed class TenantValidationMiddleware : IMiddleware
{
    /// <summary>The webhook path this middleware inspects. Other paths bypass the check.</summary>
    public const string WebhookPath = "/api/messages";

    private readonly IOptionsMonitor<TeamsMessagingOptions> _options;
    private readonly ILogger<TenantValidationMiddleware> _logger;

    /// <summary>
    /// Initialize a new <see cref="TenantValidationMiddleware"/>.
    /// </summary>
    /// <param name="options">Options snapshot carrying the tenant allow-list.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public TenantValidationMiddleware(
        IOptionsMonitor<TeamsMessagingOptions> options,
        ILogger<TenantValidationMiddleware> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// ASP.NET Core middleware entry point.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="next">The next middleware in the ASP.NET Core pipeline.</param>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (next is null) throw new ArgumentNullException(nameof(next));

        // Only inspect bot-framework webhooks. Health probes, metrics, and other endpoints
        // pass straight through.
        if (!context.Request.Path.HasValue ||
            !string.Equals(context.Request.Path.Value, WebhookPath, StringComparison.OrdinalIgnoreCase) ||
            !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var allowList = _options.CurrentValue.AllowedTenantIds;
        if (allowList is null || allowList.Count == 0)
        {
            // Misconfiguration — fail closed.
            _logger.LogWarning(
                "TenantValidationMiddleware: AllowedTenantIds is empty; rejecting all tenant traffic until configuration is supplied.");
            await WriteForbiddenAsync(context, "Tenant allow-list is empty.").ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        var tenantId = await TenantIdExtractor
            .GetOrExtractFromBodyAsync(context, context.RequestAborted)
            .ConfigureAwait(false);
        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            _logger.LogWarning(
                "TenantValidationMiddleware: inbound activity has no tenant ID; rejecting with 403.");
            await WriteForbiddenAsync(context, "Tenant ID missing from inbound activity.").ConfigureAwait(false);
            return;
        }

        if (!allowList.Any(t => string.Equals(t, tenantId, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "TenantValidationMiddleware: tenant '{TenantId}' is not in the allow-list; rejecting with 403.",
                tenantId);
            await WriteForbiddenAsync(context, "Tenant is not authorized.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(reason).ConfigureAwait(false);
    }
}
