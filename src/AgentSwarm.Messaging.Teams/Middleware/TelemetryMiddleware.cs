using System.Diagnostics;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using BotActivity = Microsoft.Bot.Schema.Activity;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Bot Framework <see cref="IMiddleware"/> that records an OpenTelemetry span for every
/// inbound activity. Span attributes: <c>activity.type</c>, <c>activity.id</c>,
/// <c>tenant.id</c>, <c>conversation.id</c>, and <c>correlation.id</c>. Per Stage 2.1, this
/// middleware runs INSIDE <c>CloudAdapter.ProcessAsync</c> after JWT validation and
/// HTTP-layer middleware (tenant + rate limit) succeed.
/// </summary>
public sealed class TelemetryMiddleware : IMiddleware
{
    /// <summary>Name of the <see cref="ActivitySource"/> used for inbound span emission.</summary>
    public const string ActivitySourceName = "AgentSwarm.Messaging.Teams.Inbound";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private readonly ILogger<TelemetryMiddleware> _logger;
    private readonly TelemetryMiddlewareOptions _options;
    private readonly HashSet<string> _sensitiveActivityTypes;

    /// <summary>Initialize a new <see cref="TelemetryMiddleware"/>.</summary>
    public TelemetryMiddleware(
        ILogger<TelemetryMiddleware> logger,
        IOptions<TelemetryMiddlewareOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value
            ?? new TelemetryMiddlewareOptions();
        _sensitiveActivityTypes = new HashSet<string>(
            _options.SensitiveActivityTypes ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task OnTurnAsync(
        ITurnContext turnContext,
        NextDelegate next,
        CancellationToken cancellationToken = default)
    {
        if (turnContext is null) throw new ArgumentNullException(nameof(turnContext));
        if (next is null) throw new ArgumentNullException(nameof(next));

        var activity = turnContext.Activity;
        var tenantId = ExtractTenantId(activity);
        var correlationId = ExtractCorrelationId(activity);

        using var span = Source.StartActivity(
            $"inbound.{activity?.Type ?? "unknown"}",
            ActivityKind.Server);

        if (span is not null)
        {
            span.SetTag("activity.type", activity?.Type ?? string.Empty);
            span.SetTag("activity.id", activity?.Id ?? string.Empty);
            span.SetTag("tenant.id", tenantId);
            span.SetTag("conversation.id", activity?.Conversation?.Id ?? string.Empty);
            span.SetTag("correlation.id", correlationId);

            if (_options.EnableDetailedPayloadCapture
                && !_sensitiveActivityTypes.Contains(activity?.Type ?? string.Empty))
            {
                span.SetTag("activity.name", activity?.Name ?? string.Empty);
                span.SetTag("activity.channel_id", activity?.ChannelId ?? string.Empty);
            }
        }

        _logger.LogDebug(
            "Inbound activity: type={ActivityType} id={ActivityId} tenant={TenantId} conversation={ConversationId} correlation={CorrelationId}",
            activity?.Type,
            activity?.Id,
            tenantId,
            activity?.Conversation?.Id,
            correlationId);

        await next(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolve the tenant ID from either <c>Conversation.TenantId</c> or
    /// <c>ChannelData.tenant.id</c>. Teams activities commonly carry the tenant in
    /// <c>ChannelData</c> only — this fallback ensures we surface a non-empty
    /// <c>tenant.id</c> span attribute in both cases.</summary>
    internal static string ExtractTenantId(BotActivity? activity)
    {
        if (activity is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(activity.Conversation?.TenantId))
        {
            return activity.Conversation!.TenantId;
        }

        // First-choice fallback: Teams strongly-typed ChannelData.
        try
        {
            var channelData = activity.GetChannelData<TeamsChannelData>();
            if (!string.IsNullOrWhiteSpace(channelData?.Tenant?.Id))
            {
                return channelData!.Tenant.Id;
            }
        }
        catch
        {
            // The strongly-typed deserialization throws when ChannelData is a JObject without
            // the expected shape (synthetic test activities). Fall through to JObject path.
        }

        // Final fallback: JObject-shaped channel data with a tenant.id property.
        if (activity.ChannelData is JObject jObj)
        {
            var tenantToken = jObj.SelectToken("tenant.id");
            if (tenantToken is not null)
            {
                var asString = tenantToken.Value<string>();
                if (!string.IsNullOrWhiteSpace(asString))
                {
                    return asString!;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractCorrelationId(BotActivity? activity)
    {
        if (activity is null) return string.Empty;

        // Prefer the W3C trace parent if present on properties; fall back to activity.Id.
        if (activity.Properties is JObject props
            && props.TryGetValue("correlationId", StringComparison.OrdinalIgnoreCase, out var token)
            && token.Type == JTokenType.String)
        {
            var value = token.Value<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return activity.Id ?? string.Empty;
    }
}
