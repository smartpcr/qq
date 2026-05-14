using System.Diagnostics;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Bot Framework <see cref="IMiddleware"/> stage that records an OpenTelemetry
/// <see cref="Activity"/> span for every inbound Bot Framework activity. The span carries
/// attributes for <c>activity.type</c>, <c>activity.id</c>, <c>tenant.id</c>,
/// <c>conversation.id</c>, and <c>correlation.id</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the FIRST Bot Framework middleware so subsequent stages
/// (<see cref="ActivityDeduplicationMiddleware"/>, the activity handler) execute inside this
/// span. Telemetry exporters configured in the Worker host (Stage 2.1 <c>Program.cs</c>)
/// publish the span downstream.
/// </para>
/// </remarks>
public sealed class TelemetryMiddleware : IMiddleware
{
    /// <summary>The name of the OpenTelemetry <see cref="ActivitySource"/> emitted by this middleware.</summary>
    public const string ActivitySourceName = "AgentSwarm.Messaging.Teams";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private readonly IOptionsMonitor<TelemetryMiddlewareOptions> _options;
    private readonly ILogger<TelemetryMiddleware> _logger;

    /// <summary>
    /// Initialize a new <see cref="TelemetryMiddleware"/>.
    /// </summary>
    /// <param name="options">Options controlling payload capture.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public TelemetryMiddleware(
        IOptionsMonitor<TelemetryMiddlewareOptions> options,
        ILogger<TelemetryMiddleware> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken = default)
    {
        if (turnContext is null) throw new ArgumentNullException(nameof(turnContext));
        if (next is null) throw new ArgumentNullException(nameof(next));

        var activity = turnContext.Activity;
        using var span = Source.StartActivity("Teams.InboundActivity", ActivityKind.Server);

        if (span is not null)
        {
            span.SetTag("messaging.system", "teams");
            span.SetTag("activity.type", activity?.Type ?? "unknown");
            span.SetTag("activity.id", activity?.Id);
            span.SetTag("conversation.id", activity?.Conversation?.Id);
            span.SetTag("tenant.id", ExtractTenantId(turnContext));
            var correlationId = ExtractCorrelationId(turnContext);
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                span.SetTag("correlation.id", correlationId);
            }

            var opts = _options.CurrentValue;
            var captureAllowed =
                opts.EnableDetailedPayloadCapture &&
                (activity?.Type is null ||
                 !opts.SensitiveActivityTypes.Any(s => string.Equals(s, activity.Type, StringComparison.OrdinalIgnoreCase)));

            if (captureAllowed && activity is not null)
            {
                span.SetTag("activity.text", activity.Text);
            }
        }

        try
        {
            await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span?.SetTag("error", true);
            span?.SetTag("error.message", ex.Message);
            _logger.LogError(ex, "Teams telemetry middleware caught an exception while processing activity {ActivityId}.", activity?.Id);
            throw;
        }
    }

    private static string? ExtractTenantId(ITurnContext turnContext)
    {
        var activity = turnContext.Activity;
        if (activity?.Conversation?.TenantId is { Length: > 0 } convTenant)
        {
            return convTenant;
        }

        // Fallback: Teams activities also carry the tenant in ChannelData ("tenant":{"id":...}).
        // The Bot Framework SDK deserializes ChannelData as a Newtonsoft.Json.Linq.JObject when
        // the activity is parsed from HTTP — and as a plain anonymous-typed object when callers
        // construct activities programmatically. Probe both shapes via the strongly-typed
        // Teams helper first (which handles JObject + JsonElement), then fall back to a
        // duck-typed read for raw dictionaries used in tests.
        if (activity is not null)
        {
            try
            {
                var teamsData = activity.GetChannelData<Microsoft.Bot.Schema.Teams.TeamsChannelData>();
                if (teamsData?.Tenant?.Id is { Length: > 0 } teamsTenant)
                {
                    return teamsTenant;
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Channel data wasn't shaped as TeamsChannelData (e.g. unit tests using an
                // anonymous object). Fall through to the duck-typed reader below.
            }
            catch (InvalidCastException)
            {
                // GetChannelData rethrows cast errors when the underlying object isn't a JObject.
            }

            if (TryReadTenantFromAnonymousChannelData(activity.ChannelData) is { Length: > 0 } anon)
            {
                return anon;
            }
        }

        return null;
    }

    private static string? TryReadTenantFromAnonymousChannelData(object? channelData)
    {
        if (channelData is null) return null;

        // Newtonsoft JObject path: most production Bot Framework HTTP requests land here.
        if (channelData is Newtonsoft.Json.Linq.JObject jo)
        {
            var jid = (string?)jo["tenant"]?["id"];
            if (!string.IsNullOrWhiteSpace(jid)) return jid;
        }

        // Reflection over an anonymous/POCO object: { tenant = new { id = "..." } } in tests.
        var tenantProp = channelData.GetType().GetProperty("tenant")
            ?? channelData.GetType().GetProperty("Tenant");
        var tenantValue = tenantProp?.GetValue(channelData);
        if (tenantValue is null) return null;

        var idProp = tenantValue.GetType().GetProperty("id")
            ?? tenantValue.GetType().GetProperty("Id");
        return idProp?.GetValue(tenantValue) as string;
    }

    private static string? ExtractCorrelationId(ITurnContext turnContext)
    {
        if (turnContext.TurnState.Get<string>("CorrelationId") is { Length: > 0 } stored)
        {
            return stored;
        }

        return turnContext.Activity?.Id;
    }
}
