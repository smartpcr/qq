using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// <see cref="IHealthCheck"/> that verifies the local bot-registration prerequisites for
/// Teams proactive messaging. Aligned with <c>implementation-plan.md</c> §5.1 step 8 and
/// <c>tech-spec.md</c> §5.1 R-5: the check does NOT call Microsoft Graph — proactive
/// messaging uses <c>BotAdapter.ContinueConversationAsync</c> with the bot's own
/// <c>MicrosoftAppId</c>.
/// </summary>
/// <remarks>
/// <para>
/// Asserted at startup and exposed via the standard ASP.NET Core health-check pipeline:
/// </para>
/// <list type="number">
///   <item><description><see cref="TeamsMessagingOptions.MicrosoftAppId"/> is configured
///   (non-empty).</description></item>
///   <item><description><see cref="BotFrameworkAuthentication"/> can acquire a token for
///   the Bot Connector service — verified by calling
///   <c>CreateConnectorClientAsync</c> with the canonical AMER service URL. A successful
///   token acquisition proves the channel-service credentials in the bot's Entra ID
///   registration are reachable.</description></item>
///   <item><description><see cref="IConversationReferenceStore"/> is reachable —
///   verified by calling <see cref="IConversationReferenceStore.GetAllActiveAsync"/>
///   with a sentinel tenant. Any exception is treated as "unhealthy persistence".</description></item>
/// </list>
/// <para>
/// Returns <see cref="HealthStatus.Healthy"/> when all probes pass; otherwise
/// <see cref="HealthStatus.Degraded"/> with a description naming the failed probe so
/// operators can triage from the health endpoint without enabling debug logs. A
/// <see cref="HealthStatus.Unhealthy"/> result is reserved for cases where the check
/// itself cannot run (e.g. cancellation by the host) — never returned for missing
/// configuration, because that is a deployment-time fault the operator must fix and
/// reporting it as <see cref="HealthStatus.Degraded"/> keeps load balancers from
/// removing the instance during a configuration rollout.
/// </para>
/// </remarks>
public sealed class TeamsAppPolicyHealthCheck : IHealthCheck
{
    /// <summary>
    /// Canonical health-check name used to register and probe this check.
    /// </summary>
    public const string Name = "teams-app-policy";

    /// <summary>
    /// Sentinel tenant the store-reachability probe runs against. Any tenant ID works
    /// — the call merely exercises the store's read path — but a stable sentinel value
    /// keeps the probe deterministic and makes the audit/store telemetry easy to filter
    /// out.
    /// </summary>
    private const string HealthProbeTenantId = "__teams-health-probe__";

    /// <summary>Canonical Teams AMER service URL used for the token-acquisition probe.</summary>
    private const string HealthProbeServiceUrl = "https://smba.trafficmanager.net/amer/";

    private readonly IOptionsMonitor<TeamsMessagingOptions> _messagingOptions;
    private readonly IOptionsMonitor<TeamsAppPolicyOptions> _policyOptions;
    private readonly BotFrameworkAuthentication _botAuthentication;
    private readonly IConversationReferenceStore _referenceStore;
    private readonly ILogger<TeamsAppPolicyHealthCheck> _logger;

    /// <summary>Construct a <see cref="TeamsAppPolicyHealthCheck"/>.</summary>
    /// <exception cref="ArgumentNullException">If any dependency is null.</exception>
    public TeamsAppPolicyHealthCheck(
        IOptionsMonitor<TeamsMessagingOptions> messagingOptions,
        IOptionsMonitor<TeamsAppPolicyOptions> policyOptions,
        BotFrameworkAuthentication botAuthentication,
        IConversationReferenceStore referenceStore,
        ILogger<TeamsAppPolicyHealthCheck> logger)
    {
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _policyOptions = policyOptions ?? throw new ArgumentNullException(nameof(policyOptions));
        _botAuthentication = botAuthentication ?? throw new ArgumentNullException(nameof(botAuthentication));
        _referenceStore = referenceStore ?? throw new ArgumentNullException(nameof(referenceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messaging = _messagingOptions.CurrentValue;
        var policy = _policyOptions.CurrentValue;
        var data = new Dictionary<string, object>
        {
            ["microsoftAppIdConfigured"] = !string.IsNullOrEmpty(messaging.MicrosoftAppId),
            ["allowedTenantCount"] = messaging.AllowedTenantIds?.Count ?? 0,
            ["requireAdminConsent"] = policy.RequireAdminConsent,
            ["blockSideloading"] = policy.BlockSideloading,
            ["allowedAppCatalogScopes"] = policy.AllowedAppCatalogScopes is null
                ? Array.Empty<string>()
                : policy.AllowedAppCatalogScopes.ToArray(),
        };

        var policyValidation = policy.Validate();
        if (policyValidation.Count > 0)
        {
            data["policyValidationErrors"] = policyValidation.ToArray();
            return HealthCheckResult.Degraded(
                description: "Teams app policy options are invalid: " + string.Join(" ", policyValidation),
                data: data);
        }

        if (string.IsNullOrEmpty(messaging.MicrosoftAppId))
        {
            return HealthCheckResult.Degraded(
                description: "Teams MicrosoftAppId is not configured; bot cannot authenticate to the channel service.",
                data: data);
        }

        // (1) BotFrameworkAuthentication token-acquisition probe. The connector factory
        // owns the credential-resolution path the runtime uses for outbound channel calls;
        // exercising CreateAsync at startup proves the bot's Entra ID registration can
        // mint a Bot Connector token (an HttpRequestException / unauthorised response
        // here surfaces as a Degraded health status rather than crashing the host).
        try
        {
            var factory = _botAuthentication.CreateConnectorFactory(
                AuthenticationProbeIdentity.AnonymousClaimsIdentity);
            using var client = await factory
                .CreateAsync(
                    serviceUrl: HealthProbeServiceUrl,
                    audience: messaging.MicrosoftAppId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            data["botFrameworkAuthentication"] = "healthy";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TeamsAppPolicyHealthCheck: BotFrameworkAuthentication.CreateConnectorClientAsync threw.");
            data["botFrameworkAuthentication"] = "unhealthy";
            data["botFrameworkAuthenticationError"] = ex.GetType().FullName ?? "Exception";
            return HealthCheckResult.Degraded(
                description: "BotFrameworkAuthentication could not acquire a Bot Connector token: " + ex.Message,
                exception: ex,
                data: data);
        }

        // (2) IConversationReferenceStore reachability probe.
        try
        {
            await _referenceStore
                .GetAllActiveAsync(HealthProbeTenantId, cancellationToken)
                .ConfigureAwait(false);
            data["conversationReferenceStore"] = "healthy";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TeamsAppPolicyHealthCheck: IConversationReferenceStore.GetAllActiveAsync threw.");
            data["conversationReferenceStore"] = "unhealthy";
            data["conversationReferenceStoreError"] = ex.GetType().FullName ?? "Exception";
            return HealthCheckResult.Degraded(
                description: "IConversationReferenceStore is unreachable: " + ex.Message,
                exception: ex,
                data: data);
        }

        return HealthCheckResult.Healthy(
            description: "Teams app policy probes passed: MicrosoftAppId configured, BotFrameworkAuthentication healthy, IConversationReferenceStore reachable.",
            data: data);
    }
}

/// <summary>
/// Internal sentinel helper exposing the anonymous <see cref="System.Security.Claims.ClaimsIdentity"/> the
/// health-check uses to drive a token request without an inbound activity context. Hosted on a
/// separate type so the field can be readonly without exposing it on the public surface of
/// <see cref="TeamsAppPolicyHealthCheck"/>.
/// </summary>
internal static class AuthenticationProbeIdentity
{
    public static System.Security.Claims.ClaimsIdentity AnonymousClaimsIdentity { get; }
        = new System.Security.Claims.ClaimsIdentity();
}
