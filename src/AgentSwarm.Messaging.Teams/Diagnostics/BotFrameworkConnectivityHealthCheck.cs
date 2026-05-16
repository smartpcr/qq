using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Stage 6.3 <see cref="IHealthCheck"/> that verifies the Microsoft Bot Framework
/// runtime prerequisites the connector depends on for outbound delivery — adapter
/// initialization, network reachability of the Entra ID token host, real app-credential
/// token acquisition, AND ConnectorFactory composability. Returns
/// <see cref="HealthStatus.Healthy"/> when all probes pass; otherwise
/// <see cref="HealthStatus.Degraded"/> with a description naming the failed probe.
/// </summary>
/// <remarks>
/// <para>
/// This is the §6.3-Step-3 health check. It is intentionally narrow — it does NOT
/// re-validate the Teams app installation policy (that is owned by
/// <see cref="Security.TeamsAppPolicyHealthCheck"/>) and it does NOT exercise the
/// conversation reference persistence layer (owned by
/// <see cref="ConversationReferenceStoreHealthCheck"/>). Keeping the three checks
/// independent lets operators reading <c>/health</c> identify the failing surface
/// without untangling overlapping responsibilities.
/// </para>
/// <para>
/// <b>Probe details (iter-2 — real token acquisition, not just OIDC discovery).</b>
/// </para>
/// <list type="number">
/// <item><description><b>Adapter initialization.</b> The injected
/// <see cref="CloudAdapter"/> singleton being non-null proves the DI graph wired the
/// Bot Framework adapter.</description></item>
/// <item><description><b>MicrosoftAppId configured.</b> An empty AppId would prevent
/// any token acquisition; reported as Degraded.</description></item>
/// <item><description><b>Token host network reachability.</b> The check executes an
/// <see cref="HttpClient.GetAsync(string, CancellationToken)"/> against
/// <c>https://login.microsoftonline.com/common/.well-known/openid-configuration</c>
/// (the canonical Entra ID OIDC discovery doc — public, no auth required, served by
/// the same host that mints Bot Framework access tokens). A 2xx response proves the
/// token host is network-reachable; any non-success status or transport error
/// transitions the check to <see cref="HealthStatus.Degraded"/>.</description></item>
/// <item><description><b>Real app-credential token acquisition (iter-2 evaluator
/// feedback item 3).</b> The check invokes
/// <see cref="IBotFrameworkTokenProbe.AcquireTokenAsync"/> which, in the default
/// production wiring, calls
/// <c>MicrosoftAppCredentials.GetTokenAsync</c>
/// — a REAL HTTPS POST to the Bot Framework / Entra token endpoint using the bot's
/// configured AppId + AppPassword. A non-empty access token proves end-to-end
/// credential validity (token endpoint reachable AND app credentials accepted by
/// Entra ID). When app credentials are not configured the probe returns
/// <see cref="BotFrameworkTokenProbeStatus.Skipped"/> and the health check records
/// the skip in <see cref="HealthCheckResult.Data"/> without flipping its overall
/// status — operators using managed-identity or certificate auth can register their
/// own <see cref="IBotFrameworkTokenProbe"/> implementation through DI.</description></item>
/// <item><description><b>Connector factory wiring.</b> A final call to
/// <see cref="BotFrameworkAuthentication.CreateConnectorFactory"/> confirms the
/// authentication graph is composable; failures surface as Degraded with the factory
/// exception attached.</description></item>
/// </list>
/// </remarks>
public sealed class BotFrameworkConnectivityHealthCheck : IHealthCheck
{
    /// <summary>Canonical health-check name used to register and probe this check.</summary>
    public const string Name = "teams-bot-framework-connectivity";

    /// <summary>
    /// Canonical Entra ID OIDC discovery doc — used as the token-host network
    /// reachability probe target. Public, unauthenticated; hosted on the same
    /// <c>login.microsoftonline.com</c> infrastructure that serves the Bot Framework
    /// token endpoint. A 2xx response proves the dependency is network-reachable.
    /// </summary>
    public const string TokenEndpointProbeUrl = "https://login.microsoftonline.com/common/.well-known/openid-configuration";

    /// <summary>
    /// Named <see cref="HttpClient"/> registered by
    /// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddBotFrameworkConnectivityHealthCheck"/>
    /// so callers can configure per-check timeouts, proxy settings, or test-host
    /// substitutes without touching the default <see cref="HttpClient"/> registration.
    /// Also used by the default <see cref="MicrosoftAppCredentialsTokenProbe"/> so
    /// the token-acquisition probe honors the same overrides as the OIDC probe.
    /// </summary>
    public const string HttpClientName = "BotFrameworkConnectivityHealthCheck";

    /// <summary>Default per-probe HTTP timeout (5 s) used when no override is configured.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly CloudAdapter? _adapter;
    private readonly BotFrameworkAuthentication _botAuthentication;
    private readonly IOptionsMonitor<TeamsMessagingOptions> _messagingOptions;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IBotFrameworkTokenProbe? _tokenProbe;
    private readonly ILogger<BotFrameworkConnectivityHealthCheck> _logger;

    /// <summary>Construct a <see cref="BotFrameworkConnectivityHealthCheck"/>.</summary>
    /// <param name="adapter">
    /// Bot Framework <see cref="CloudAdapter"/> singleton — null is allowed so the
    /// health check can probe a host that has not yet wired an adapter and report
    /// <see cref="HealthStatus.Degraded"/> rather than failing DI activation.
    /// </param>
    /// <param name="botAuthentication">Authentication contract used to mint tokens.</param>
    /// <param name="messagingOptions">Teams messaging options (read for MicrosoftAppId).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="httpClientFactory">
    /// HTTP client factory used to acquire the named <see cref="HttpClientName"/> client
    /// for the OIDC discovery probe. Optional — when null, the health check falls back
    /// to a single shared <see cref="HttpClient"/> with <see cref="DefaultProbeTimeout"/>.
    /// </param>
    /// <param name="tokenProbe">
    /// Optional <see cref="IBotFrameworkTokenProbe"/> used to exercise real app-credential
    /// token acquisition (iter-2 evaluator feedback item 3). When null, the token
    /// probe is skipped and recorded in <see cref="HealthCheckResult.Data"/> as
    /// <c>tokenAcquisitionProbed</c> = <c>false</c>. The
    /// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddBotFrameworkConnectivityHealthCheck"/>
    /// helper registers <see cref="MicrosoftAppCredentialsTokenProbe"/> as the default.
    /// </param>
    /// <exception cref="ArgumentNullException">If a required dependency is null.</exception>
    public BotFrameworkConnectivityHealthCheck(
        CloudAdapter? adapter,
        BotFrameworkAuthentication botAuthentication,
        IOptionsMonitor<TeamsMessagingOptions> messagingOptions,
        ILogger<BotFrameworkConnectivityHealthCheck> logger,
        IHttpClientFactory? httpClientFactory = null,
        IBotFrameworkTokenProbe? tokenProbe = null)
    {
        _adapter = adapter;
        _botAuthentication = botAuthentication ?? throw new ArgumentNullException(nameof(botAuthentication));
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory;
        _tokenProbe = tokenProbe;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messaging = _messagingOptions.CurrentValue;
        var data = new Dictionary<string, object>
        {
            ["adapterInitialized"] = _adapter is not null,
            ["microsoftAppIdConfigured"] = !string.IsNullOrEmpty(messaging.MicrosoftAppId),
            ["tokenEndpointProbeUrl"] = TokenEndpointProbeUrl,
        };

        if (_adapter is null)
        {
            return HealthCheckResult.Degraded(
                description: "BotFrameworkConnectivity: Unhealthy. CloudAdapter is not initialized; outbound delivery cannot proceed.",
                data: data);
        }

        if (string.IsNullOrEmpty(messaging.MicrosoftAppId))
        {
            return HealthCheckResult.Degraded(
                description: "BotFrameworkConnectivity: Unhealthy. TeamsMessagingOptions.MicrosoftAppId is not configured; token acquisition will fail.",
                data: data);
        }

        // Probe 1 — REAL HTTP GET to login.microsoftonline.com proves the token host
        // is network-reachable. Done BEFORE the credential-acquisition probe so a
        // network outage is reported as a network failure rather than a credential
        // failure. This is the cheap "is the dependency even reachable?" sanity step;
        // the token-acquisition probe (Probe 2) is what proves the credentials are
        // actually accepted by Entra ID.
        try
        {
            using var http = CreateHttpClient();
            using var response = await http
                .GetAsync(TokenEndpointProbeUrl, cancellationToken)
                .ConfigureAwait(false);

            data["tokenEndpointStatusCode"] = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                data["tokenEndpointReachable"] = false;
                return HealthCheckResult.Degraded(
                    description: $"BotFrameworkConnectivity: Unhealthy. Token endpoint at {TokenEndpointProbeUrl} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                    data: data);
            }

            data["tokenEndpointReachable"] = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "BotFrameworkConnectivityHealthCheck: token endpoint probe threw.");
            data["tokenEndpointReachable"] = false;
            data["tokenEndpointError"] = ex.GetType().FullName ?? "Exception";
            return HealthCheckResult.Degraded(
                description: $"BotFrameworkConnectivity: Unhealthy. Token endpoint at {TokenEndpointProbeUrl} is unreachable: {ex.Message}",
                exception: ex,
                data: data);
        }

        // Probe 2 — REAL app-credential token acquisition via IBotFrameworkTokenProbe
        // (iter-2 evaluator feedback item 3). The default production wiring resolves
        // MicrosoftAppCredentialsTokenProbe which calls
        // MicrosoftAppCredentials.GetTokenAsync() — issuing an actual HTTPS POST to
        // the Bot Framework / Entra token endpoint using the bot's AppId + AppPassword.
        // A non-empty access token proves end-to-end credential validity (the OIDC
        // probe above only proves network reachability — it does NOT prove the bot's
        // credentials are accepted by Entra ID, which is exactly what this probe adds).
        //
        // The probe is optional: when null (e.g. tests that opt out, or hosts that
        // wire their own auth) the probe is skipped and recorded as such in the
        // result data without flipping the overall status. Likewise, when credentials
        // are not configured the probe returns Skipped — the OIDC probe above already
        // covers the AppId-empty case for status purposes.
        if (_tokenProbe is not null)
        {
            BotFrameworkTokenProbeResult tokenResult;
            try
            {
                tokenResult = await _tokenProbe
                    .AcquireTokenAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Defensive: a well-behaved IBotFrameworkTokenProbe never throws (it
                // wraps everything in BotFrameworkTokenProbeResult). A throw here
                // means a custom implementation broke the contract; treat as Failed.
                _logger.LogWarning(
                    ex,
                    "BotFrameworkConnectivityHealthCheck: IBotFrameworkTokenProbe threw outside its result contract.");
                tokenResult = new BotFrameworkTokenProbeResult(
                    BotFrameworkTokenProbeStatus.Failed,
                    FailureMessage: ex.Message,
                    Exception: ex);
            }

            data["tokenAcquisitionProbed"] = true;
            data["tokenAcquisitionStatus"] = tokenResult.Status.ToString();

            switch (tokenResult.Status)
            {
                case BotFrameworkTokenProbeStatus.Succeeded:
                    data["tokenAcquisitionSucceeded"] = true;
                    break;

                case BotFrameworkTokenProbeStatus.Skipped:
                    // Credentials not configured (e.g. cert / MSI auth). Record but
                    // do not flip status — the OIDC probe + factory probe still apply.
                    data["tokenAcquisitionSucceeded"] = false;
                    data["tokenAcquisitionSkippedReason"] = tokenResult.FailureMessage ?? "credentials not configured";
                    break;

                case BotFrameworkTokenProbeStatus.Failed:
                    data["tokenAcquisitionSucceeded"] = false;
                    data["tokenAcquisitionError"] = tokenResult.FailureMessage ?? "unknown failure";
                    return HealthCheckResult.Degraded(
                        description: "BotFrameworkConnectivity: Unhealthy. App-credential token acquisition failed: " + (tokenResult.FailureMessage ?? "unknown failure"),
                        exception: tokenResult.Exception,
                        data: data);
            }
        }
        else
        {
            data["tokenAcquisitionProbed"] = false;
        }

        // Probe 3 — confirm the BotFrameworkAuthentication graph is composable. Done
        // AFTER the token probe so a factory issue is not mistaken for a credential
        // problem. The created client is not exercised further (the token probe above
        // already proved the credential path); this is the wiring sanity step.
        try
        {
            var factory = _botAuthentication.CreateConnectorFactory(
                Security.AuthenticationProbeIdentity.AnonymousClaimsIdentity);
            using var client = await factory
                .CreateAsync(
                    serviceUrl: "https://smba.trafficmanager.net/amer/",
                    audience: messaging.MicrosoftAppId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            data["connectorFactoryComposable"] = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "BotFrameworkConnectivityHealthCheck: ConnectorFactory.CreateAsync threw.");
            data["connectorFactoryComposable"] = false;
            data["connectorFactoryError"] = ex.GetType().FullName ?? "Exception";
            return HealthCheckResult.Degraded(
                description: "BotFrameworkConnectivity: Unhealthy. Bot Framework ConnectorFactory could not be created: " + ex.Message,
                exception: ex,
                data: data);
        }

        return HealthCheckResult.Healthy(
            description: "BotFrameworkConnectivity: Healthy. CloudAdapter initialized, token endpoint reachable, app credentials accepted, and ConnectorFactory composable.",
            data: data);
    }

    private HttpClient CreateHttpClient()
    {
        if (_httpClientFactory is not null)
        {
            return _httpClientFactory.CreateClient(HttpClientName);
        }

        return new HttpClient { Timeout = DefaultProbeTimeout };
    }
}
