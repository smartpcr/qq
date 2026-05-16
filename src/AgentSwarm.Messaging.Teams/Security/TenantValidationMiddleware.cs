using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Security;

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
/// Only requests to the configured bot webhook endpoint are inspected; other endpoints
/// (health probes, metrics) bypass the check entirely. The webhook path is taken from
/// <see cref="TeamsMessagingOptions.BotEndpoint"/> on every request (via
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>) and falls back to
/// <see cref="DefaultBotEndpointPath"/> when <see cref="TeamsMessagingOptions.BotEndpoint"/>
/// is unset, so operator-configured endpoints are honored without code changes.
/// </para>
/// <para>
/// The middleware buffers the request body with
/// <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/> before reading,
/// so <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/> can re-read
/// the same payload downstream.
/// </para>
/// <para>
/// <b>Tenant ID casing invariant.</b> The allow-list comparison is performed with
/// <see cref="StringComparison.OrdinalIgnoreCase"/>. Azure AD tenant IDs are GUIDs whose
/// textual casing varies by source (Bot Framework activity payloads, Entra ID
/// <c>tid</c> claims, operator-supplied configuration), so a case-sensitive comparison
/// here would reject traffic the JWT-layer companion check accepts. The JWT-layer
/// validator
/// <see cref="EntraTenantAwareClaimsValidator.ValidateClaimsAsync(System.Collections.Generic.IList{System.Security.Claims.Claim})"/>
/// already compares tenants with <see cref="StringComparison.OrdinalIgnoreCase"/>; using
/// the same comparer in this HTTP-layer middleware keeps the two defence-in-depth layers
/// in sync so a tenant cased differently from the allow-list entry cannot be accepted by
/// one layer and rejected by the other (or vice versa when
/// <c>EntraBotFrameworkAuthenticationOptions.RequireTenantClaim</c> is <c>false</c>).
/// Future changes MUST preserve this symmetry — if one layer's comparer is changed the
/// other MUST be changed in lockstep.
/// </para>
/// <para>
/// Audit logging at this stage is limited to <see cref="ILogger"/> warnings; Stage 5.1
/// upgrades this same middleware to emit a <c>SecurityRejection</c> audit entry via
/// <c>IAuditLogger</c>.
/// </para>
/// </remarks>
public sealed class TenantValidationMiddleware : IMiddleware
{
    /// <summary>
    /// Default webhook path used when <see cref="TeamsMessagingOptions.BotEndpoint"/> is
    /// unset. Matches the Bot Framework convention.
    /// </summary>
    public const string DefaultBotEndpointPath = "/api/messages";

    /// <summary>
    /// Canonical <see cref="StringComparison"/> used for tenant-ID allow-list lookups.
    /// MUST stay in lockstep with
    /// <see cref="EntraTenantAwareClaimsValidator"/>'s comparer (currently
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>) so the HTTP-layer and JWT-layer
    /// defence-in-depth checks accept and reject the same set of tenant-ID casings.
    /// Centralising the constant here makes the cross-layer contract visible to future
    /// reviewers and prevents a one-sided regression.
    /// </summary>
    internal const StringComparison TenantIdComparison = StringComparison.OrdinalIgnoreCase;

    private readonly IOptionsMonitor<TeamsMessagingOptions> _options;
    private readonly ILogger<TenantValidationMiddleware> _logger;

    /// <summary>
    /// Initialize a new <see cref="TenantValidationMiddleware"/>.
    /// </summary>
    /// <param name="options">Options snapshot carrying the tenant allow-list and the
    /// configured bot endpoint.</param>
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

        var snapshot = CopyTeamsMessagingOptions(_options.CurrentValue);

        // Only inspect bot-framework webhooks. Health probes, metrics, and other endpoints
        // pass straight through.
        if (!IsBotWebhookPath(context.Request, snapshot))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var allowList = snapshot.AllowedTenantIds;
        if (allowList is null || allowList.Count == 0)
        {
            // Misconfiguration — fail closed.
            _logger.LogWarning(
                "TenantValidationMiddleware: AllowedTenantIds is empty; rejecting all tenant traffic until configuration is supplied.");
            await WriteForbiddenAsync(context, "Tenant allow-list is empty.").ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        var tenantId = await ExtractTenantIdAsync(context).ConfigureAwait(false);
        context.Request.Body.Position = 0;

        if (string.IsNullOrEmpty(tenantId))
        {
            _logger.LogWarning(
                "TenantValidationMiddleware: inbound activity has no tenant ID; rejecting with 403.");
            await WriteForbiddenAsync(context, "Tenant ID missing from inbound activity.").ConfigureAwait(false);
            return;
        }

        // Use TenantIdComparison (OrdinalIgnoreCase) so this HTTP-layer check accepts the
        // same casing variants that EntraTenantAwareClaimsValidator's JWT-layer check
        // accepts. Azure AD tenant IDs are GUIDs whose textual casing varies by source;
        // a case-sensitive comparer here would silently diverge from the JWT layer and
        // produce mismatched accept/reject decisions across the two defence-in-depth
        // layers. See the class-level "Tenant ID casing invariant" remarks for the full
        // contract.
        if (!allowList.Any(t => string.Equals(t, tenantId, TenantIdComparison)))
        {
            _logger.LogWarning(
                "TenantValidationMiddleware: tenant '{TenantId}' is not in the allow-list; rejecting with 403.",
                tenantId);
            await WriteForbiddenAsync(context, "Tenant is not authorized.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> when the inbound request targets the bot webhook endpoint
    /// configured on <see cref="TeamsMessagingOptions.BotEndpoint"/>. When
    /// <see cref="TeamsMessagingOptions.BotEndpoint"/> is unset, falls back to
    /// <see cref="DefaultBotEndpointPath"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="TeamsMessagingOptions.BotEndpoint"/> may be configured as either an
    /// absolute URI (e.g., <c>https://bot.example.com/api/messages</c>) or a relative path
    /// (e.g., <c>/api/messages</c>). Both forms are supported: absolute URIs have their
    /// <see cref="Uri.AbsolutePath"/> extracted, relative values are used directly. The
    /// resolved path is compared case-insensitively against the request path, and only
    /// POST requests are treated as bot webhook traffic.
    /// </remarks>
    private static bool IsBotWebhookPath(HttpRequest request, TeamsMessagingOptions options)
    {
        if (!request.Path.HasValue || !HttpMethods.IsPost(request.Method))
        {
            return false;
        }

        var configuredPath = ResolveBotWebhookPath(options);
        return string.Equals(
            request.Path.Value,
            configuredPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the bot webhook path from <see cref="TeamsMessagingOptions.BotEndpoint"/>,
    /// falling back to <see cref="DefaultBotEndpointPath"/> when the option is unset or
    /// cannot be parsed.
    /// </summary>
    private static string ResolveBotWebhookPath(TeamsMessagingOptions options)
    {
        var configured = options.BotEndpoint;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultBotEndpointPath;
        }

        // Absolute URI (e.g., https://bot.example.com/api/messages) — extract path.
        if (Uri.TryCreate(configured, UriKind.Absolute, out var absolute))
        {
            var path = absolute.AbsolutePath;
            return string.IsNullOrEmpty(path) || path == "/" ? DefaultBotEndpointPath : path;
        }

        // Relative path (e.g., /api/messages or api/messages). Normalize to a leading slash
        // so it compares cleanly against HttpRequest.Path.Value.
        return configured.StartsWith('/') ? configured : "/" + configured;
    }

    /// <summary>
    /// Take a defensive copy of the live options snapshot so callers observe a consistent
    /// view across the request, even if the underlying configuration reloads mid-request.
    /// </summary>
    private static TeamsMessagingOptions CopyTeamsMessagingOptions(TeamsMessagingOptions source)
    {
        return new TeamsMessagingOptions
        {
            MicrosoftAppId = source.MicrosoftAppId,
            MicrosoftAppPassword = source.MicrosoftAppPassword,
            MicrosoftAppTenantId = source.MicrosoftAppTenantId,
            AllowedTenantIds = source.AllowedTenantIds is null
                ? new List<string>()
                : new List<string>(source.AllowedTenantIds),
            BotEndpoint = source.BotEndpoint,
            RateLimitPerTenantPerMinute = source.RateLimitPerTenantPerMinute,
            DeduplicationTtlMinutes = source.DeduplicationTtlMinutes,
            MaxRetryAttempts = source.MaxRetryAttempts,
            RetryBaseDelaySeconds = source.RetryBaseDelaySeconds,
        };
    }

    /// <summary>
    /// Extract the inbound activity's tenant ID from the buffered JSON body. Returns
    /// <c>null</c> when the payload cannot be parsed or no tenant ID is present.
    /// </summary>
    private static async Task<string?> ExtractTenantIdAsync(HttpContext context)
    {
        if (context.Request.ContentLength == 0)
        {
            return null;
        }

        try
        {
            context.Request.Body.Position = 0;
            using var doc = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
            var root = doc.RootElement;

            // Bot Framework activity ChannelData.tenant.id (Teams-specific).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("channelData", out var channelData) &&
                channelData.ValueKind == JsonValueKind.Object &&
                channelData.TryGetProperty("tenant", out var tenant) &&
                tenant.ValueKind == JsonValueKind.Object &&
                tenant.TryGetProperty("id", out var tenantIdProp) &&
                tenantIdProp.ValueKind == JsonValueKind.String)
            {
                var fromChannelData = tenantIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(fromChannelData))
                {
                    return fromChannelData;
                }
            }

            // Conversation.TenantId fallback.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("conversation", out var conversation) &&
                conversation.ValueKind == JsonValueKind.Object &&
                conversation.TryGetProperty("tenantId", out var convTenant) &&
                convTenant.ValueKind == JsonValueKind.String)
            {
                return convTenant.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(reason).ConfigureAwait(false);
    }
}
