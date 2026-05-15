using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// ASP.NET Core HTTP middleware enforcing per-tenant inbound rate limiting using a
/// sliding-window counter. Aligned with <c>architecture.md</c> §5.1 and the Stage 2.1
/// implementation plan.
/// </summary>
/// <remarks>
/// <para>
/// Runs after <see cref="TenantValidationMiddleware"/> and before
/// <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/>. Requests above
/// the configured quota receive HTTP 429 with a <c>Retry-After</c> header expressed in
/// whole seconds (rounded up from the window remainder).
/// </para>
/// <para>
/// The middleware operates as an ASP.NET Core HTTP middleware rather than a Bot Framework
/// <see cref="Microsoft.Bot.Builder.IMiddleware"/> because Bot Framework middleware cannot
/// influence the HTTP status code returned by <c>CloudAdapter.ProcessAsync</c> (which always
/// returns HTTP 200 for processed activities).
/// </para>
/// </remarks>
public sealed class RateLimitMiddleware : IMiddleware
{
    private readonly IOptionsMonitor<TeamsMessagingOptions> _options;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _counters =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialize a new <see cref="RateLimitMiddleware"/>.
    /// </summary>
    /// <param name="options">Options snapshot carrying the rate limit.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public RateLimitMiddleware(
        IOptionsMonitor<TeamsMessagingOptions> options,
        ILogger<RateLimitMiddleware> logger)
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

        if (!context.Request.Path.HasValue ||
            !string.Equals(context.Request.Path.Value, TenantValidationMiddleware.WebhookPath, StringComparison.OrdinalIgnoreCase) ||
            !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var limit = _options.CurrentValue.RateLimitPerTenantPerMinute;
        if (limit <= 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Body.CanSeek)
        {
            context.Request.EnableBuffering();
        }

        var tenantId = await TenantIdExtractor
            .TryExtractFromBodyAsync(context, context.RequestAborted)
            .ConfigureAwait(false);
        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            // No tenant means we cannot key the counter — let the request through so
            // downstream tenant validation handles it (this path should already have been
            // rejected by TenantValidationMiddleware in normal operation).
            await next(context).ConfigureAwait(false);
            return;
        }

        var counter = _counters.GetOrAdd(tenantId, _ =>
            new SlidingWindowCounter(limit, TimeSpan.FromMinutes(1)));

        if (!counter.TryAcquire(DateTimeOffset.UtcNow, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            context.Response.ContentType = "text/plain; charset=utf-8";
            _logger.LogWarning(
                "RateLimitMiddleware: tenant '{TenantId}' exceeded {Limit}/min; HTTP 429 with Retry-After={RetryAfterSeconds}s.",
                tenantId,
                limit,
                seconds);
            await context.Response.WriteAsync("Rate limit exceeded for tenant.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
