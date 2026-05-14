using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// ASP.NET Core HTTP middleware that enforces per-tenant inbound rate limits. Runs in the
/// ASP.NET Core request pipeline AFTER <see cref="TenantValidationMiddleware"/> and BEFORE
/// <c>CloudAdapter.ProcessAsync</c>. Returns HTTP 429 with a <c>Retry-After</c> header when
/// the tenant exceeds <see cref="TeamsMessagingOptions.RateLimitPerTenantPerMinute"/>.
/// </summary>
/// <remarks>
/// This is an ASP.NET Core middleware (not a Bot Framework <c>IMiddleware</c>) because
/// throttling must control the HTTP status code — <c>CloudAdapter</c> always returns 200 for
/// processed activities, so a Bot Framework middleware cannot surface 429 to Teams.
/// </remarks>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly IOptionsMonitor<TeamsMessagingOptions> _options;
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _counters = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initialize a new <see cref="RateLimitMiddleware"/>.</summary>
    public RateLimitMiddleware(
        RequestDelegate next,
        ILogger<RateLimitMiddleware> logger,
        IOptionsMonitor<TeamsMessagingOptions> options)
        : this(next, logger, options, clock: null)
    {
    }

    internal RateLimitMiddleware(
        RequestDelegate next,
        ILogger<RateLimitMiddleware> logger,
        IOptionsMonitor<TeamsMessagingOptions> options,
        Func<DateTimeOffset>? clock)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>ASP.NET Core middleware entry point.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (!context.Request.Path.StartsWithSegments("/api/messages")
            || !HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        var tenantId = await TryExtractTenantIdAsync(context.Request).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // Tenant resolution failures are the tenant middleware's responsibility — let
            // the pipeline continue here and the request will already have been rejected if
            // applicable. If tenant middleware is disabled, we still let it through.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var settings = _options.CurrentValue;
        var limit = settings.RateLimitPerTenantPerMinute;
        var counter = _counters.GetOrAdd(tenantId, _ => new SlidingWindowCounter(TimeSpan.FromMinutes(1)));
        var now = _clock();

        if (!counter.TryRecord(limit, now))
        {
            var retryAfter = counter.ComputeRetryAfterSeconds(now);
            _logger.LogWarning(
                "Rate limit exceeded for tenant '{TenantId}': limit={Limit}/min, retryAfter={RetryAfterSeconds}s",
                tenantId,
                limit,
                retryAfter);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

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
            if (root is not JObject obj) return null;

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
