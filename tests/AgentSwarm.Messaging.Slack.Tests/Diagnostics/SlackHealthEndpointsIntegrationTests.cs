// -----------------------------------------------------------------------
// <copyright file="SlackHealthEndpointsIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Stage 7.3 evaluator iter-2 item 4: end-to-end integration tests for
/// the readiness (<c>/health/ready</c>) and liveness (<c>/health/live</c>)
/// endpoints mounted by
/// <see cref="SlackHealthChecksServiceCollectionExtensions.MapSlackHealthEndpoints"/>
/// inside the canonical Worker composition root
/// (<see cref="Program.BuildApp"/>).
/// </summary>
/// <remarks>
/// <para>
/// Earlier iterations covered each health check via direct unit-test
/// activation. That left two operational gaps invisible to the suite:
/// </para>
/// <list type="bullet">
///   <item><description>The canonical Worker host had no
///   <c>ISlackOutboundQueue</c> registration, so
///   <see cref="SlackOutboundQueueDepthHealthCheck"/> would throw at
///   request time on activation (resolved by wiring
///   <c>AddFileSystemSlackOutboundQueue</c> +
///   <c>AddFileSystemSlackDeadLetterQueue</c> in Program.cs against
///   the durable directories from <c>Slack:Outbound:JournalDirectory</c>
///   and <c>Slack:Outbound:DeadLetterDirectory</c>).</description></item>
///   <item><description>The startup-diagnostics path logged
///   <c>Unknown</c> for transport classification because no
///   <c>ISlackInboundTransportFactory</c> was registered in the
///   Worker (resolved by inferring the transport kind directly from
///   the workspace config).</description></item>
/// </list>
/// <para>
/// Hitting the real endpoints via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> closes both gaps:
/// activation failures, missing DI registrations, and predicate-tag
/// drift surface as HTTP 5xx / unexpected status codes instead of
/// disappearing into a unit-test seam.
/// </para>
/// </remarks>
public sealed class SlackHealthEndpointsIntegrationTests : IDisposable
{
    private readonly string sqliteDirectory;
    private readonly string sqlitePath;

    public SlackHealthEndpointsIntegrationTests()
    {
        this.sqliteDirectory = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-7.3-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.sqliteDirectory);
        this.sqlitePath = Path.Combine(this.sqliteDirectory, "audit.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.sqliteDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // Best-effort cleanup; do not fail the test on a stuck file
            // handle from the host SQLite connection.
        }
    }

    [Fact]
    public async Task Live_endpoint_returns_200_even_when_no_workspaces_are_registered()
    {
        // Liveness MUST be insensitive to downstream-dependency state.
        // Per the brief, Kubernetes uses /health/live to decide whether
        // to RESTART the pod -- a Slack outage MUST NOT trigger a
        // restart, only an out-of-rotation.
        using HealthEndpointsFactory factory = new(this.sqlitePath, seedWorkspace: false);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the liveness endpoint runs no checks and MUST stay 200 as long as the process is reachable");
    }

    [Fact]
    public async Task Ready_endpoint_returns_503_when_no_workspaces_are_registered()
    {
        // Stage 7.3 evaluator iter-2 item 3 end-to-end: with zero
        // workspaces, SlackApiConnectivityHealthCheck returns
        // Unhealthy. The readiness endpoint MUST surface that as
        // HTTP 503 so Kubernetes removes the pod from rotation until
        // an operator configures a workspace. This also verifies
        // item 1: SlackOutboundQueueDepthHealthCheck and
        // SlackDeadLetterQueueDepthHealthCheck are DI-resolvable
        // (their queue dependencies live in the Worker composition
        // root after the Stage 7.3 iter-3 wiring change).
        using HealthEndpointsFactory factory = new(this.sqlitePath, seedWorkspace: false);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "no workspaces means the connectivity check is Unhealthy and readiness MUST be 503");
    }

    [Fact]
    public async Task Ready_endpoint_returns_200_when_workspace_present_and_authtest_succeeds()
    {
        // Stage 7.3 brief scenario end-to-end: a working Slack
        // connection + healthy queue depths -> /health/ready returns
        // 200. The auth tester is stubbed because the integration
        // host cannot reach api.slack.com from the test sandbox.
        using HealthEndpointsFactory factory = new(
            this.sqlitePath,
            seedWorkspace: true,
            configureServices: services =>
            {
                services.RemoveAll<ISlackAuthTester>();
                services.AddSingleton<ISlackAuthTester>(new StubAuthTester(IsHealthy: true));
            });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a workspace with successful auth.test and clean queues MUST surface as readiness OK");
    }

    [Fact]
    public async Task Ready_endpoint_returns_503_when_dlq_depth_exceeds_threshold()
    {
        // Stage 7.3 brief scenario end-to-end: DLQ depth above
        // SlackHealthCheckOptions.DeadLetterUnhealthyThreshold makes
        // the DLQ check Unhealthy; the readiness endpoint MUST
        // surface that as HTTP 503. Also verifies item 1: the DLQ
        // service registration is reachable from the health check
        // through the host's DI container.
        using HealthEndpointsFactory factory = new(
            this.sqlitePath,
            seedWorkspace: true,
            configureServices: services =>
            {
                services.RemoveAll<ISlackAuthTester>();
                services.AddSingleton<ISlackAuthTester>(new StubAuthTester(IsHealthy: true));

                // Override the DLQ with a fake reporting depth above
                // the default threshold (100). RemoveAll guarantees
                // the test fake wins over the durable
                // FileSystemSlackDeadLetterQueue Program.cs wired
                // by default.
                services.RemoveAll<ISlackDeadLetterQueue>();
                services.AddSingleton<ISlackDeadLetterQueue>(new OverflowingDeadLetterQueue(Depth: 150));
            });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "DLQ depth above threshold MUST surface as HTTP 503 on the readiness probe");
    }

    [Fact]
    public async Task Ready_endpoint_body_is_json_listing_every_slack_check_with_status()
    {
        // Stage 7.3 evaluator iter-3 item 3 / e2e Scenario 20.2:
        // /health/ready MUST return a JSON payload that names every
        // Slack health check and reports its individual status. The
        // default MapHealthChecks writer emits a single
        // "Healthy"/"Unhealthy" plain-text body, which fails the
        // scenario's per-component table assertion. This test pins
        // the JSON shape so a future writer swap cannot silently
        // drop the per-check breakdown.
        using HealthEndpointsFactory factory = new(
            this.sqlitePath,
            seedWorkspace: true,
            configureServices: services =>
            {
                services.RemoveAll<ISlackAuthTester>();
                services.AddSingleton<ISlackAuthTester>(new StubAuthTester(IsHealthy: true));
            });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be(
            "application/json",
            "the readiness endpoint MUST emit JSON so the per-check breakdown can be parsed by operator tooling");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be(
            "Healthy",
            "aggregate status MUST be the first-class field in the readiness payload");

        JsonElement checks = root.GetProperty("checks");
        checks.ValueKind.Should().Be(JsonValueKind.Array);

        // Materialise the per-check names so we can assert each
        // expected Slack check appears regardless of registration
        // order.
        List<string> checkNames = new();
        List<string> checkStatuses = new();
        foreach (JsonElement entry in checks.EnumerateArray())
        {
            checkNames.Add(entry.GetProperty("name").GetString()!);
            checkStatuses.Add(entry.GetProperty("status").GetString()!);
        }

        // Every Stage 7.3 check MUST appear in the readiness JSON,
        // each tagged with a per-component status. Names mirror
        // e2e-scenarios.md Scenario 20.2's "Slack API connectivity /
        // Outbound queue depth / DLQ depth" rows.
        checkNames.Should().Contain(SlackApiConnectivityHealthCheck.CheckName,
            "the readiness payload MUST surface the Slack API connectivity check by name");
        checkNames.Should().Contain(SlackOutboundQueueDepthHealthCheck.CheckName,
            "the readiness payload MUST surface the outbound queue-depth check by name");
        checkNames.Should().Contain(SlackDeadLetterQueueDepthHealthCheck.CheckName,
            "the readiness payload MUST surface the DLQ-depth check by name");

        checkStatuses.Should().AllSatisfy(s => s.Should().BeOneOf("Healthy", "Degraded", "Unhealthy"));
        checkStatuses.Should().AllBe("Healthy",
            "with a stub-healthy auth tester and empty queues every per-check status should resolve to Healthy");
    }

    [Fact]
    public async Task Ready_endpoint_json_payload_surfaces_individual_unhealthy_check()
    {
        // Companion to the all-healthy JSON test: under failure the
        // payload MUST identify WHICH Slack check went Unhealthy so
        // an operator can triage without re-running probes by hand.
        using HealthEndpointsFactory factory = new(
            this.sqlitePath,
            seedWorkspace: true,
            configureServices: services =>
            {
                services.RemoveAll<ISlackAuthTester>();
                services.AddSingleton<ISlackAuthTester>(new StubAuthTester(IsHealthy: true));

                services.RemoveAll<ISlackDeadLetterQueue>();
                services.AddSingleton<ISlackDeadLetterQueue>(new OverflowingDeadLetterQueue(Depth: 150));
            });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("Unhealthy");

        bool foundDlqUnhealthy = false;
        foreach (JsonElement entry in root.GetProperty("checks").EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()!;
            string status = entry.GetProperty("status").GetString()!;
            if (name == SlackDeadLetterQueueDepthHealthCheck.CheckName)
            {
                status.Should().Be("Unhealthy",
                    "the DLQ check MUST resolve to Unhealthy when its probe reports depth above the threshold");
                foundDlqUnhealthy = true;
            }
        }

        foundDlqUnhealthy.Should().BeTrue(
            "the DLQ check MUST appear in the readiness payload so operators can confirm WHICH check tripped the 503");
    }

    private sealed class HealthEndpointsFactory : WebApplicationFactory<Program>
    {
        private const string TestTeamId = "T01HEALTH001";

        private readonly string sqlitePath;
        private readonly bool seedWorkspace;
        private readonly Action<IServiceCollection>? configureServices;

        public HealthEndpointsFactory(
            string sqlitePath,
            bool seedWorkspace,
            Action<IServiceCollection>? configureServices = null)
        {
            this.sqlitePath = sqlitePath;
            this.seedWorkspace = seedWorkspace;
            this.configureServices = configureServices;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // "Testing" -- NOT "Production" -- so
            // EnsureDurableInboundQueueForProduction stays a no-op
            // (the test boots the in-process channel queue) and the
            // host doesn't refuse to start.
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                // Each test gets a unique temp root for the durable
                // file-system queue + DLQ + inbound DLQ sink so the
                // Worker host's data/ defaults don't bleed between
                // parallel test runs and post-test cleanup is
                // localised to one Dispose() call.
                string queueRoot = Path.Combine(
                    Path.GetDirectoryName(this.sqlitePath)!,
                    "queues");

                Dictionary<string, string?> overrides = new()
                {
                    // Pin the InMemory secret provider so the eager
                    // resolution in Program.BuildApp doesn't try to
                    // hit the process env / Key Vault.
                    ["SecretProvider:ProviderType"] = "InMemory",

                    // Isolated SQLite per test instance.
                    ["ConnectionStrings:" + Program.SlackAuditConnectionStringKey] =
                        $"Data Source={this.sqlitePath}",

                    // Isolated durable-queue directories per test
                    // instance. Program.BuildApp wires the
                    // file-system outbound queue + DLQ + inbound
                    // dead-letter sink from these paths; without
                    // the overrides every test would share the
                    // process-wide "data/" defaults.
                    ["Slack:Outbound:JournalDirectory"] =
                        Path.Combine(queueRoot, "outbound-journal"),
                    ["Slack:Outbound:DeadLetterDirectory"] =
                        Path.Combine(queueRoot, "outbound-dead-letter"),
                    ["Slack:Inbound:DeadLetterDirectory"] =
                        Path.Combine(queueRoot, "inbound-dead-letter"),
                };

                if (this.seedWorkspace)
                {
                    overrides["Slack:Workspaces:0:TeamId"] = TestTeamId;
                    overrides["Slack:Workspaces:0:WorkspaceName"] = "Stage 7.3 Health Endpoints Test Workspace";
                    overrides["Slack:Workspaces:0:SigningSecretRef"] = "test://signing-secret/" + TestTeamId;
                    overrides["Slack:Workspaces:0:BotTokenSecretRef"] = "test://bot-token/" + TestTeamId;
                    overrides["Slack:Workspaces:0:DefaultChannelId"] = "C01HEALTH001";
                    overrides["Slack:Workspaces:0:AllowedChannelIds:0"] = "C01HEALTH001";
                    overrides["Slack:Workspaces:0:AllowedUserGroupIds:0"] = "S01HEALTH001";
                    overrides["Slack:Workspaces:0:Enabled"] = "true";
                }

                cfg.AddInMemoryCollection(overrides);
            });

            if (this.configureServices is not null)
            {
                builder.ConfigureServices(this.configureServices);
            }
        }
    }

    private sealed record StubAuthTester(bool IsHealthy) : ISlackAuthTester
    {
        public Task<SlackAuthTestResult> TestAsync(string teamId, CancellationToken ct)
            => Task.FromResult(new SlackAuthTestResult(
                TeamId: teamId,
                IsHealthy: this.IsHealthy,
                Detail: this.IsHealthy ? "stubbed ok" : "stubbed fail",
                ErrorCode: this.IsHealthy ? null : "invalid_auth"));
    }

    private sealed record OverflowingDeadLetterQueue(int Depth) : ISlackDeadLetterQueue, ISlackDeadLetterQueueDepthProbe
    {
        public int GetCurrentDepth() => this.Depth;

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(Array.Empty<SlackDeadLetterEntry>());
    }
}
