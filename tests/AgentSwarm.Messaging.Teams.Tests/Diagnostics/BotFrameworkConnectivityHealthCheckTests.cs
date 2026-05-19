using System.Diagnostics;
using System.Net;
using System.Net.Http;
using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Tests.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="BotFrameworkConnectivityHealthCheck"/>. Drives each of
/// the §6.3-Step-3 probes (adapter init, MicrosoftAppId presence, REAL token endpoint
/// HTTP reachability against <c>login.microsoftonline.com</c>, ConnectorFactory
/// composability) using a recording <see cref="HttpMessageHandler"/> so the test
/// never touches the network.
/// </summary>
public sealed class BotFrameworkConnectivityHealthCheckTests
{
    private const string MicrosoftAppId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task CheckHealthAsync_AdapterNull_ReturnsDegraded()
    {
        var check = new BotFrameworkConnectivityHealthCheck(
            adapter: null,
            botAuthentication: new SecurityTestDoubles.FakeBotFrameworkAuthentication(),
            messagingOptions: BuildOptionsMonitor(),
            logger: NullLogger<BotFrameworkConnectivityHealthCheck>.Instance,
            httpClientFactory: new FakeHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Unhealthy", result.Description);
        Assert.Contains("CloudAdapter is not initialized", result.Description);
        Assert.False((bool)result.Data["adapterInitialized"]);
    }

    [Fact]
    public async Task CheckHealthAsync_MicrosoftAppIdMissing_ReturnsDegraded()
    {
        var check = BuildCheck(microsoftAppId: string.Empty);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Unhealthy", result.Description);
        Assert.Contains("MicrosoftAppId is not configured", result.Description);
        Assert.False((bool)result.Data["microsoftAppIdConfigured"]);
    }

    [Fact]
    public async Task CheckHealthAsync_TokenEndpointReturnsNonSuccess_ReturnsDegraded()
    {
        var check = BuildCheck(
            httpFactory: new FakeHttpClientFactory(req =>
            {
                Assert.Equal(BotFrameworkConnectivityHealthCheck.TokenEndpointProbeUrl, req.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    ReasonPhrase = "Server Error",
                };
            }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Unhealthy", result.Description);
        Assert.Contains("Token endpoint", result.Description);
        Assert.Contains("HTTP 500", result.Description);
        Assert.False((bool)result.Data["tokenEndpointReachable"]);
        Assert.Equal(500, result.Data["tokenEndpointStatusCode"]);
    }

    [Fact]
    public async Task CheckHealthAsync_TokenEndpointThrowsTransportError_ReturnsDegraded()
    {
        var transportError = new HttpRequestException("DNS failure");
        var check = BuildCheck(
            httpFactory: new FakeHttpClientFactory(_ => throw transportError));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Unhealthy", result.Description);
        Assert.Contains("Token endpoint", result.Description);
        Assert.Contains("unreachable", result.Description);
        Assert.False((bool)result.Data["tokenEndpointReachable"]);
        Assert.NotNull(result.Exception);
        Assert.Same(transportError, result.Exception);
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectorFactoryThrows_ReturnsDegraded()
    {
        var auth = new SecurityTestDoubles.FakeBotFrameworkAuthentication
        {
            CreateAsyncThrow = new InvalidOperationException("factory composition broken"),
        };
        var check = BuildCheck(auth: auth);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Unhealthy", result.Description);
        Assert.Contains("ConnectorFactory could not be created", result.Description);
        Assert.False((bool)result.Data["connectorFactoryComposable"]);
        // Token endpoint must have been probed AND passed before the factory probe ran.
        Assert.True((bool)result.Data["tokenEndpointReachable"]);
    }

    [Fact]
    public async Task CheckHealthAsync_HappyPath_ReturnsHealthy()
    {
        var probeUrlObserved = false;
        var check = BuildCheck(
            httpFactory: new FakeHttpClientFactory(req =>
            {
                probeUrlObserved = req.RequestUri!.ToString() == BotFrameworkConnectivityHealthCheck.TokenEndpointProbeUrl;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Healthy", result.Description);
        Assert.True((bool)result.Data["adapterInitialized"]);
        Assert.True((bool)result.Data["microsoftAppIdConfigured"]);
        Assert.True((bool)result.Data["tokenEndpointReachable"]);
        Assert.Equal(200, result.Data["tokenEndpointStatusCode"]);
        Assert.True((bool)result.Data["connectorFactoryComposable"]);
        Assert.True(probeUrlObserved, "Health check did not probe the canonical token endpoint URL.");
    }

    [Fact]
    public async Task CheckHealthAsync_HostCancellation_PropagatesOperationCanceled()
    {
        var check = BuildCheck();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public void Constructor_NullAuthentication_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BotFrameworkConnectivityHealthCheck(
            adapter: null,
            botAuthentication: null!,
            messagingOptions: BuildOptionsMonitor(),
            logger: NullLogger<BotFrameworkConnectivityHealthCheck>.Instance));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BotFrameworkConnectivityHealthCheck(
            adapter: null,
            botAuthentication: new SecurityTestDoubles.FakeBotFrameworkAuthentication(),
            messagingOptions: null!,
            logger: NullLogger<BotFrameworkConnectivityHealthCheck>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BotFrameworkConnectivityHealthCheck(
            adapter: null,
            botAuthentication: new SecurityTestDoubles.FakeBotFrameworkAuthentication(),
            messagingOptions: BuildOptionsMonitor(),
            logger: null!));
    }

    [Fact]
    public void TokenEndpointProbeUrl_TargetsCanonicalEntraIdOidcDiscovery()
    {
        // Pinned: the §6.3 contract is "token endpoint reachability" and the canonical
        // Bot Framework token host is login.microsoftonline.com. Any change to the probe
        // URL must be intentional — the test enshrines the host so a refactor can't
        // silently retarget the probe at an unrelated endpoint.
        Assert.Equal(
            "https://login.microsoftonline.com/common/.well-known/openid-configuration",
            BotFrameworkConnectivityHealthCheck.TokenEndpointProbeUrl);
    }

    // ── iter-2 evaluator feedback item 3 — REAL app-credential token acquisition ──

    [Fact]
    public async Task CheckHealthAsync_TokenProbeNotInjected_RecordsSkippedAndStaysHealthy()
    {
        // When no IBotFrameworkTokenProbe is wired (e.g. tests opting out, or hosts
        // that take over auth themselves) the health check records the skip in the
        // result data but does NOT flip status. The OIDC + factory probes still apply.
        var check = BuildCheck(tokenProbe: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False((bool)result.Data["tokenAcquisitionProbed"]);
    }

    [Fact]
    public async Task CheckHealthAsync_TokenProbeSucceeds_HealthyAndRecordedInData()
    {
        var probe = new RecordingTokenProbe(
            new BotFrameworkTokenProbeResult(BotFrameworkTokenProbeStatus.Succeeded));
        var check = BuildCheck(tokenProbe: probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True((bool)result.Data["tokenAcquisitionProbed"]);
        Assert.True((bool)result.Data["tokenAcquisitionSucceeded"]);
        Assert.Equal(nameof(BotFrameworkTokenProbeStatus.Succeeded), result.Data["tokenAcquisitionStatus"]);
        Assert.Equal(1, probe.InvocationCount);
    }

    [Fact]
    public async Task CheckHealthAsync_TokenProbeFails_ReturnsDegradedWithCanonicalDescription()
    {
        var probeException = new InvalidOperationException("AADSTS7000215: invalid_client");
        var probe = new RecordingTokenProbe(
            new BotFrameworkTokenProbeResult(
                BotFrameworkTokenProbeStatus.Failed,
                FailureMessage: probeException.Message,
                Exception: probeException));
        var check = BuildCheck(tokenProbe: probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("BotFrameworkConnectivity: Unhealthy", result.Description);
        Assert.Contains("App-credential token acquisition failed", result.Description);
        Assert.Contains("AADSTS7000215", result.Description);
        Assert.True((bool)result.Data["tokenAcquisitionProbed"]);
        Assert.False((bool)result.Data["tokenAcquisitionSucceeded"]);
        Assert.Equal(nameof(BotFrameworkTokenProbeStatus.Failed), result.Data["tokenAcquisitionStatus"]);
        Assert.Same(probeException, result.Exception);
    }

    [Fact]
    public async Task CheckHealthAsync_TokenProbeSkipped_StaysHealthyAndRecordsReason()
    {
        // Credential-less scenarios (cert auth, MSI) — probe reports Skipped, health
        // stays Healthy, the skip reason is preserved in the data dictionary so
        // operators can see WHY token acquisition was bypassed.
        var probe = new RecordingTokenProbe(
            new BotFrameworkTokenProbeResult(
                BotFrameworkTokenProbeStatus.Skipped,
                FailureMessage: "no password configured"));
        var check = BuildCheck(tokenProbe: probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True((bool)result.Data["tokenAcquisitionProbed"]);
        Assert.False((bool)result.Data["tokenAcquisitionSucceeded"]);
        Assert.Equal(nameof(BotFrameworkTokenProbeStatus.Skipped), result.Data["tokenAcquisitionStatus"]);
        Assert.Equal("no password configured", result.Data["tokenAcquisitionSkippedReason"]);
    }

    [Fact]
    public async Task CheckHealthAsync_TokenProbeThrowsOutsideContract_TreatedAsFailedNotPropagated()
    {
        // Defensive contract: a misbehaving custom IBotFrameworkTokenProbe that throws
        // instead of returning a Failed result should NOT crash the health check —
        // operators expect /health to report Degraded, not throw 500.
        var probe = new ThrowingTokenProbe(new ApplicationException("buggy probe"));
        var check = BuildCheck(tokenProbe: probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("App-credential token acquisition failed", result.Description);
        Assert.Contains("buggy probe", result.Description);
        Assert.False((bool)result.Data["tokenAcquisitionSucceeded"]);
        Assert.IsType<ApplicationException>(result.Exception);
    }

    [Fact]
    public async Task MicrosoftAppCredentialsTokenProbe_SharedSecret_NoPasswordConfigured_ReturnsFailed()
    {
        // Iter-4 evaluator feedback item 1 — a SharedSecret bot (the default
        // AuthenticationMode) with a missing MicrosoftAppPassword is misconfigured
        // and MUST surface as Failed (Degraded on /health) rather than silently
        // skipping. Prior to iter-4 this path returned Skipped and kept health
        // green, hiding a production-fatal config bug.
        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = MicrosoftAppId,
            MicrosoftAppPassword = string.Empty,
            AuthenticationMode = TeamsAuthenticationMode.SharedSecret,
        };
        var probe = new MicrosoftAppCredentialsTokenProbe(
            messagingOptions: new StaticOptionsMonitor<TeamsMessagingOptions>(options));

        var result = await probe.AcquireTokenAsync(CancellationToken.None);

        Assert.Equal(BotFrameworkTokenProbeStatus.Failed, result.Status);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("MicrosoftAppPassword", result.FailureMessage);
        Assert.Contains("SharedSecret", result.FailureMessage);
    }

    [Fact]
    public async Task MicrosoftAppCredentialsTokenProbe_SharedSecret_NoAppIdConfigured_ReturnsFailed()
    {
        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = string.Empty,
            MicrosoftAppPassword = "non-empty",
            AuthenticationMode = TeamsAuthenticationMode.SharedSecret,
        };
        var probe = new MicrosoftAppCredentialsTokenProbe(
            messagingOptions: new StaticOptionsMonitor<TeamsMessagingOptions>(options));

        var result = await probe.AcquireTokenAsync(CancellationToken.None);

        Assert.Equal(BotFrameworkTokenProbeStatus.Failed, result.Status);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("MicrosoftAppId", result.FailureMessage);
        Assert.Contains("SharedSecret", result.FailureMessage);
    }

    [Theory]
    [InlineData(TeamsAuthenticationMode.Certificate)]
    [InlineData(TeamsAuthenticationMode.ManagedIdentity)]
    [InlineData(TeamsAuthenticationMode.WorkloadFederated)]
    public async Task MicrosoftAppCredentialsTokenProbe_NonSharedSecretMode_NoPasswordConfigured_ReturnsSkipped(
        TeamsAuthenticationMode mode)
    {
        // Iter-4 — cert / managed-identity / federated hosts legitimately have no
        // AppPassword. The default probe MUST report Skipped (not Failed) for
        // these modes so health stays green and the host's custom probe (which
        // exercises the real cert/MSI path) owns the credential health signal.
        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = MicrosoftAppId,
            MicrosoftAppPassword = string.Empty,
            AuthenticationMode = mode,
        };
        var probe = new MicrosoftAppCredentialsTokenProbe(
            messagingOptions: new StaticOptionsMonitor<TeamsMessagingOptions>(options));

        var result = await probe.AcquireTokenAsync(CancellationToken.None);

        Assert.Equal(BotFrameworkTokenProbeStatus.Skipped, result.Status);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains(mode.ToString(), result.FailureMessage);
    }

    [Fact]
    public async Task BotFrameworkConnectivityHealthCheck_SharedSecret_MissingPassword_ReportsDegraded()
    {
        // Iter-4 — end-to-end regression: the default DI registration (SharedSecret
        // + no password) MUST flip /health to Degraded, not Healthy. This is the
        // exact path the evaluator flagged in iter-3 feedback item 1.
        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = MicrosoftAppId,
            MicrosoftAppPassword = string.Empty,
            AuthenticationMode = TeamsAuthenticationMode.SharedSecret,
        };
        var probe = new MicrosoftAppCredentialsTokenProbe(
            messagingOptions: new StaticOptionsMonitor<TeamsMessagingOptions>(options));
        var check = BuildCheck(tokenProbe: probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("App-credential token acquisition failed", result.Description);
        Assert.False((bool)result.Data["tokenAcquisitionSucceeded"]);
    }

    [Fact]
    public void MicrosoftAppCredentialsTokenProbe_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MicrosoftAppCredentialsTokenProbe(messagingOptions: null!));
    }

    // -----------------------------------------------------------------------------------
    // Iter-6 regression — race-against-cancellation guard added in iter-5
    // (src/AgentSwarm.Messaging.Teams/Diagnostics/IBotFrameworkTokenProbe.cs:182-219).
    // The Bot Framework SDK's MicrosoftAppCredentials.GetTokenAsync does NOT accept
    // a CancellationToken, so without the explicit Task.WhenAny guard a hanging
    // Entra ID token endpoint (or a paused mock handler) would block the probe
    // indefinitely and defeat the host's /health timeout. These tests pin the
    // guard's contract end-to-end through the public probe surface.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task MicrosoftAppCredentialsTokenProbe_HangingEndpoint_CancellationTokenAborts()
    {
        // Given a fake HttpClient whose handler never returns and a cancellation
        // token that fires after ~250 ms, AcquireTokenAsync MUST surface an
        // OperationCanceledException within a few seconds — not block on the
        // hanging HTTP call. A failure here means the iter-5 cancellation race
        // has regressed and /health probes can hang indefinitely behind a slow
        // Entra ID endpoint.
        var options = new TeamsMessagingOptions
        {
            // Use a unique AppId per test run so the SDK's process-wide
            // MicrosoftAppCredentials token cache cannot short-circuit
            // GetTokenAsync from a sibling test's cached entry.
            MicrosoftAppId = $"hang-{Guid.NewGuid():N}",
            MicrosoftAppPassword = "non-empty-password",
            AuthenticationMode = TeamsAuthenticationMode.SharedSecret,
        };
        var probe = new MicrosoftAppCredentialsTokenProbe(
            messagingOptions: new StaticOptionsMonitor<TeamsMessagingOptions>(options),
            httpClientFactory: new HangingHttpClientFactory());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.AcquireTokenAsync(cts.Token));
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(10),
            $"AcquireTokenAsync must honor the CancellationToken within seconds; observed elapsed={sw.Elapsed.TotalSeconds:F2}s. " +
            "If this assertion fires the iter-5 race-against-cancellation guard in IBotFrameworkTokenProbe.cs has regressed.");
    }

    [Fact]
    public async Task MicrosoftAppCredentialsTokenProbe_PrefiredCancellationToken_ThrowsBeforeNetworkCall()
    {
        // The early ThrowIfCancellationRequested above the SDK call (line 170 of
        // IBotFrameworkTokenProbe.cs) must short-circuit before any HTTP work
        // happens when the caller passes an already-cancelled token. The
        // HangingHttpClientFactory's invocation counter proves no HTTP call
        // was initiated (otherwise the test would either hang or count > 0).
        var hangingFactory = new HangingHttpClientFactory();
        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = $"prefire-{Guid.NewGuid():N}",
            MicrosoftAppPassword = "non-empty-password",
            AuthenticationMode = TeamsAuthenticationMode.SharedSecret,
        };
        var probe = new MicrosoftAppCredentialsTokenProbe(
            messagingOptions: new StaticOptionsMonitor<TeamsMessagingOptions>(options),
            httpClientFactory: hangingFactory);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.AcquireTokenAsync(cts.Token));

        Assert.Equal(0, hangingFactory.SendCount);
    }

    private static BotFrameworkConnectivityHealthCheck BuildCheck(
        Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter? adapter = null,
        Microsoft.Bot.Connector.Authentication.BotFrameworkAuthentication? auth = null,
        string? microsoftAppId = MicrosoftAppId,
        IHttpClientFactory? httpFactory = null,
        IBotFrameworkTokenProbe? tokenProbe = null)
    {
        adapter ??= new TeamsMessengerConnectorTests.RecordingCloudAdapter();
        auth ??= new SecurityTestDoubles.FakeBotFrameworkAuthentication();
        httpFactory ??= new FakeHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        return new BotFrameworkConnectivityHealthCheck(
            adapter: adapter,
            botAuthentication: auth,
            messagingOptions: BuildOptionsMonitor(microsoftAppId),
            logger: NullLogger<BotFrameworkConnectivityHealthCheck>.Instance,
            httpClientFactory: httpFactory,
            tokenProbe: tokenProbe);
    }

    private static IOptionsMonitor<TeamsMessagingOptions> BuildOptionsMonitor(string? microsoftAppId = MicrosoftAppId)
    {
        var options = new TeamsMessagingOptions { MicrosoftAppId = microsoftAppId ?? string.Empty };
        return new StaticOptionsMonitor<TeamsMessagingOptions>(options);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Stand-in <see cref="IHttpClientFactory"/> that hands out an <see cref="HttpClient"/>
    /// backed by a delegate handler — keeps the health check off the real network.
    /// </summary>
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpClient CreateClient(string name)
            => new(new DelegateHandler(_responder)) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class DelegateHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_responder(request));
            }
        }
    }

    /// <summary>
    /// Iter-6 — HTTP client factory whose handler returns a never-completing
    /// <see cref="Task{HttpResponseMessage}"/>. Used to simulate a hanging Entra ID
    /// token endpoint so the iter-5 race-against-cancellation guard in
    /// <see cref="MicrosoftAppCredentialsTokenProbe.AcquireTokenAsync"/> can be
    /// pinned by a dedicated regression test. <see cref="SendCount"/> exposes how
    /// many times the handler was actually invoked so a sibling test can prove the
    /// pre-fire cancellation path short-circuits BEFORE any HTTP call is started.
    /// </summary>
    private sealed class HangingHttpClientFactory : IHttpClientFactory
    {
        private readonly HangingHandler _handler = new();

        public int SendCount => _handler.SendCount;

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };

        private sealed class HangingHandler : HttpMessageHandler
        {
            private int _sendCount;

            public int SendCount => Volatile.Read(ref _sendCount);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _sendCount);
                var tcs = new TaskCompletionSource<HttpResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(
                    static state => ((TaskCompletionSource<HttpResponseMessage>)state!)
                        .TrySetCanceled(),
                    tcs))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Recording <see cref="IBotFrameworkTokenProbe"/> that returns a pre-canned result
    /// on every invocation — used to drive the iter-2 evaluator-item-3 probe paths
    /// (success / failure / skipped) without touching the network.
    /// </summary>
    private sealed class RecordingTokenProbe : IBotFrameworkTokenProbe
    {
        private readonly BotFrameworkTokenProbeResult _result;
        public int InvocationCount { get; private set; }

        public RecordingTokenProbe(BotFrameworkTokenProbeResult result) => _result = result;

        public Task<BotFrameworkTokenProbeResult> AcquireTokenAsync(CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(_result);
        }
    }

    /// <summary>
    /// Probe that throws OUTSIDE the BotFrameworkTokenProbeResult contract — used to
    /// verify the health check's defensive wrap-and-translate behavior so a misbehaving
    /// custom probe cannot crash /health.
    /// </summary>
    private sealed class ThrowingTokenProbe : IBotFrameworkTokenProbe
    {
        private readonly Exception _exception;
        public ThrowingTokenProbe(Exception exception) => _exception = exception;
        public Task<BotFrameworkTokenProbeResult> AcquireTokenAsync(CancellationToken cancellationToken)
            => throw _exception;
    }
}
