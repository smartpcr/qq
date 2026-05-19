using System.Net;
using System.Text.Json;
using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Tests.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Stage 6.3 end-to-end integration tests for the <c>/health</c> HTTP surface mapped
/// by <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.MapTeamsHealthChecks"/>.
/// Where the <see cref="ConversationReferenceStoreHealthCheckTests"/> unit tests
/// exercise the <see cref="IHealthCheck"/> in isolation, these tests drive the full
/// ASP.NET Core pipeline — DI composition, endpoint routing, status-code mapping, and
/// the JSON response writer — so the §6.3 scenario
/// (<i>"When <c>/health</c> is called, Then it returns <c>Degraded</c> with detail
/// <c>ConversationReferenceStore: Unhealthy</c>"</i>) is verified at the HTTP boundary
/// the brief actually names, not at the in-process check boundary.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the pipeline in <see cref="TestServer"/> so each test issues a real HTTP
/// request against an in-memory transport. Only the
/// <see cref="ConversationReferenceStoreHealthCheck"/> is registered — the sibling
/// <see cref="BotFrameworkConnectivityHealthCheck"/> requires a real
/// <c>CloudAdapter</c> + <c>BotFrameworkAuthentication</c> graph which is out of
/// scope for this integration slice (it is exercised by its own unit suite).
/// </para>
/// </remarks>
public sealed class TeamsHealthCheckEndpointIntegrationTests : IAsyncLifetime
{
    private IHost? _host;
    private HttpClient? _client;
    private SecurityTestDoubles.StubConversationReferenceStore _store = null!;

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    /// <summary>
    /// Stage 6.3 Scenario 3 (HTTP form) — Given <see cref="IConversationReferenceStore.CountActiveAsync"/>
    /// throws (i.e. database is unreachable), When <c>GET /health</c> is called via the
    /// real ASP.NET pipeline, Then the HTTP response is 503 Service Unavailable AND the
    /// JSON body contains the canonical substring <c>ConversationReferenceStore: Unhealthy</c>.
    /// </summary>
    [Fact]
    public async Task GetHealth_StoreThrows_ReturnsServiceUnavailableWithCanonicalDescription()
    {
        await StartHostAsync(store => store.CountActiveAsyncThrow = new InvalidOperationException("db-down"));

        var response = await _client!.GetAsync("/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ConversationReferenceStore: Unhealthy", body);

        var envelope = JsonSerializer.Deserialize<HealthEnvelope>(body, JsonOptions);
        Assert.NotNull(envelope);
        Assert.Equal("Degraded", envelope!.Status);
        Assert.NotNull(envelope.Checks);
        var entry = Assert.Single(envelope.Checks!);
        Assert.Equal(ConversationReferenceStoreHealthCheck.Name, entry.Name);
        Assert.Equal("Degraded", entry.Status);
        Assert.NotNull(entry.Description);
        Assert.StartsWith(
            ConversationReferenceStoreHealthCheck.UnhealthyDescriptionPrefix,
            entry.Description!,
            StringComparison.Ordinal);
        Assert.Contains("db-down", entry.Description);
    }

    /// <summary>
    /// Counter-scenario — Given the reference store is reachable and reports a real
    /// count, When <c>GET /health</c> is called, Then the response is 200 OK with the
    /// per-check status reported as Healthy. Guards against false positives where the
    /// pipeline always reports Degraded.
    /// </summary>
    [Fact]
    public async Task GetHealth_StoreReachable_ReturnsOkWithHealthyEnvelope()
    {
        await StartHostAsync(store => store.CountActiveResult = 7L);

        var response = await _client!.GetAsync("/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<HealthEnvelope>(body, JsonOptions);
        Assert.NotNull(envelope);
        Assert.Equal("Healthy", envelope!.Status);
        var entry = Assert.Single(envelope.Checks!);
        Assert.Equal("Healthy", entry.Status);
        Assert.Contains("7 active reference", entry.Description);
    }

    /// <summary>
    /// Stage 6.3 iter-9 evaluator fix item 3 — pins the per-check <c>data</c> field
    /// on the HTTP envelope so <see cref="ConversationReferenceStoreHealthCheck"/>'s
    /// <c>referenceCount</c> diagnostic is structurally visible to <c>/health</c>
    /// callers. The §6.3 step 4 requirement ("verify database connectivity AND
    /// reference count") was previously satisfied at the in-process
    /// <see cref="HealthReportEntry.Data"/> boundary only — the prior JSON writer
    /// dropped <c>HealthReportEntry.Data</c> so HTTP probes could not see the count.
    /// This test fails on the pre-iter-9 writer (the <c>data</c> property is absent /
    /// null) and passes on the iter-9 writer that promotes
    /// <see cref="HealthReportEntry.Data"/> to a top-level <c>data</c> JSON object.
    /// </summary>
    [Fact]
    public async Task GetHealth_StoreReachable_JsonBodyExposesReferenceCountFromEntryData()
    {
        await StartHostAsync(store => store.CountActiveResult = 42L);

        var response = await _client!.GetAsync("/health", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<HealthEnvelope>(body, JsonOptions);
        Assert.NotNull(envelope);
        var entry = Assert.Single(envelope!.Checks!);
        Assert.NotNull(entry.Data);
        Assert.True(entry.Data!.ContainsKey("referenceCount"),
            $"Expected per-check 'data.referenceCount' on the /health JSON envelope; raw body: {body}");

        // The Data dictionary on HealthReportEntry stores object?, which System.Text.Json
        // serializes as JsonElement on round-trip. The value must reflect the real count
        // returned by the stubbed store (42), not the unsupported sentinel.
        var refCountElement = (JsonElement)entry.Data!["referenceCount"]!;
        Assert.Equal(JsonValueKind.Number, refCountElement.ValueKind);
        Assert.Equal(42L, refCountElement.GetInt64());
    }

    /// <summary>
    /// Stage 6.3 iter-9 evaluator fix item 3 (degraded counterpart) — even on the
    /// degraded path, the per-check <c>data</c> envelope carries the diagnostic
    /// <c>error</c> entry the health check populates so operators can triage from
    /// the HTTP body. Pins that the new writer treats both healthy and degraded
    /// entries identically with respect to <see cref="HealthReportEntry.Data"/>.
    /// </summary>
    [Fact]
    public async Task GetHealth_StoreThrows_JsonBodyExposesErrorTypeFromEntryData()
    {
        await StartHostAsync(store => store.CountActiveAsyncThrow = new InvalidOperationException("db-down"));

        var response = await _client!.GetAsync("/health", CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<HealthEnvelope>(body, JsonOptions);
        Assert.NotNull(envelope);
        var entry = Assert.Single(envelope!.Checks!);
        Assert.NotNull(entry.Data);
        Assert.True(entry.Data!.ContainsKey("error"),
            $"Expected per-check 'data.error' on the /health JSON envelope; raw body: {body}");
        var errorElement = (JsonElement)entry.Data!["error"]!;
        Assert.Equal(JsonValueKind.String, errorElement.ValueKind);
        Assert.Contains("InvalidOperationException", errorElement.GetString());
    }

    /// <summary>
    /// Liveness contract — Given the database is unreachable (which flips
    /// <c>/health</c> to Degraded), When <c>GET /health/live</c> is called, Then the
    /// response is still 200 OK because liveness must only fail when the process
    /// itself is wedged. This is the k8s liveness vs. readiness convention.
    /// </summary>
    [Fact]
    public async Task GetHealthLive_StoreUnreachable_ReturnsOkBecauseLivenessIgnoresDependencies()
    {
        await StartHostAsync(store => store.CountActiveAsyncThrow = new InvalidOperationException("db-down"));

        var liveResponse = await _client!.GetAsync("/health/live", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);

        var liveBody = await liveResponse.Content.ReadAsStringAsync();
        var liveEnvelope = JsonSerializer.Deserialize<HealthEnvelope>(liveBody, JsonOptions);
        Assert.NotNull(liveEnvelope);
        Assert.Equal("Healthy", liveEnvelope!.Status);
        Assert.Empty(liveEnvelope.Checks!);

        // Sanity: readiness alias DOES flip — proving the predicate filter actually
        // takes effect on /health/live but not on /health/ready.
        var readyResponse = await _client.GetAsync("/health/ready", CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);
        var readyBody = await readyResponse.Content.ReadAsStringAsync();
        Assert.Contains("ConversationReferenceStore: Unhealthy", readyBody);
    }

    /// <summary>
    /// Custom-prefix smoke test — Given the helper is mapped at <c>/api/health</c>
    /// rather than the default <c>/health</c>, When the custom path is called, Then
    /// the response writer still emits the canonical envelope and the status code
    /// map still applies.
    /// </summary>
    [Fact]
    public async Task MapTeamsHealthChecks_CustomPrefix_RoutesUnderSuppliedPath()
    {
        await StartHostAsync(
            configureStore: store => store.CountActiveResult = 1L,
            pathPrefix: "/api/health");

        var response = await _client!.GetAsync("/api/health", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var liveResponse = await _client.GetAsync("/api/health/live", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);

        var defaultResponse = await _client.GetAsync("/health", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, defaultResponse.StatusCode);
    }

    private async Task StartHostAsync(
        Action<SecurityTestDoubles.StubConversationReferenceStore> configureStore,
        string pathPrefix = "/health")
    {
        _store = new SecurityTestDoubles.StubConversationReferenceStore();
        configureStore(_store);

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IConversationReferenceStore>(_store);
                    services.AddConversationReferenceStoreHealthCheck();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapTeamsHealthChecks(pathPrefix));
                });
            });

        _host = await builder.StartAsync();
        _client = _host.GetTestClient();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class HealthEnvelope
    {
        public string? Status { get; set; }
        public double TotalDurationMs { get; set; }
        public List<HealthCheckEnvelope>? Checks { get; set; }
    }

    private sealed class HealthCheckEnvelope
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? Description { get; set; }
        public double DurationMs { get; set; }
        public List<string>? Tags { get; set; }
        public Dictionary<string, object?>? Data { get; set; }
        public string? Exception { get; set; }
    }
}
