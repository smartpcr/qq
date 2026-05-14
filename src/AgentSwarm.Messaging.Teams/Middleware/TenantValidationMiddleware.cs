using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// ASP.NET Core HTTP middleware that enforces the tenant allow-list configured on
/// <see cref="TeamsMessagingOptions.AllowedTenantIds"/>. Runs in the ASP.NET Core request
/// pipeline BEFORE <c>CloudAdapter.ProcessAsync</c> so the connector never sees activities
/// from disallowed tenants and so the rejection produces HTTP 403 (rather than the HTTP 200
/// that <c>CloudAdapter</c> always emits for processed activities). JWT authentication is a
/// separate concern handled by <c>CloudAdapter</c>'s built-in <c>JwtTokenValidation</c>.
/// </summary>
/// <remarks>
/// <para>
/// The full inbound pipeline order is:
/// <see cref="TenantValidationMiddleware"/> (403) → <see cref="RateLimitMiddleware"/> (429)
/// → <c>CloudAdapter.ProcessAsync</c> (which performs JWT validation → 401, then Bot
/// Framework middleware: telemetry → dedup → handler).
/// </para>
/// <para>
/// Audit logging escalates from <see cref="ILogger"/> warnings here to
/// <c>IAuditLogger.LogAsync(SecurityRejection)</c> in Stage 5.1.
/// </para>
/// </remarks>
public sealed class TenantValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantValidationMiddleware> _logger;
    private readonly IOptionsMonitor<TeamsMessagingOptions> _options;

    /// <summary>Initialize a new <see cref="TenantValidationMiddleware"/>.</summary>
    public TenantValidationMiddleware(
        RequestDelegate next,
        ILogger<TenantValidationMiddleware> logger,
        IOptionsMonitor<TeamsMessagingOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>ASP.NET Core middleware entry point.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        // Only validate POST /api/messages — health probes and other endpoints pass through.
        if (!context.Request.Path.StartsWithSegments("/api/messages")
            || !HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        var tenantId = await TryExtractTenantIdAsync(context.Request).ConfigureAwait(false);

        var allowed = _options.CurrentValue.AllowedTenantIds ?? Array.Empty<string>();
        var allowedSet = new HashSet<string>(
            allowed.Where(t => !string.IsNullOrWhiteSpace(t)),
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(tenantId) || !allowedSet.Contains(tenantId))
        {
            _logger.LogWarning(
                "Rejecting inbound activity: tenant '{TenantId}' is not in the configured allow-list. AllowedCount={AllowedCount}",
                tenantId ?? "<missing>",
                allowedSet.Count);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Reset request body for the next reader.
        context.Request.Body.Position = 0;
        await _next(context).ConfigureAwait(false);
    }

    private static async Task<string?> TryExtractTenantIdAsync(HttpRequest request)
    {
        try
        {
            using var reader = new StreamReader(
                request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var root = JToken.Parse(body);
            if (root is not JObject obj)
            {
                return null;
            }

            // Prefer channelData.tenant.id (Teams-native location), fall back to
            // conversation.tenantId (Bot Framework canonical).
            var channelDataTenant = obj.SelectToken("channelData.tenant.id")?.Value<string>();
            if (!string.IsNullOrWhiteSpace(channelDataTenant))
            {
                return channelDataTenant;
            }

            var conversationTenant = obj.SelectToken("conversation.tenantId")?.Value<string>();
            return string.IsNullOrWhiteSpace(conversationTenant) ? null : conversationTenant;
        }
        catch
        {
            return null;
        }
    }
}
