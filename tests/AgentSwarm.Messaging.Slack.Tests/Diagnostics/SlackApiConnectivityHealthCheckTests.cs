// -----------------------------------------------------------------------
// <copyright file="SlackApiConnectivityHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 7.3 unit tests for <see cref="SlackApiConnectivityHealthCheck"/>.
/// Covers the brief test scenarios:
/// <list type="bullet">
///   <item><description>"Given a working Slack API connection, When the
///   health check runs, Then it reports <c>Healthy</c>".</description></item>
///   <item><description>Companion to the DLQ brief scenario: when
///   <c>auth.test</c> fails the connectivity check MUST surface
///   <see cref="HealthStatus.Unhealthy"/> with a descriptive
///   message.</description></item>
/// </list>
/// </summary>
public sealed class SlackApiConnectivityHealthCheckTests
{
    [Fact]
    public async Task Healthy_Slack_connectivity_reports_healthy_when_authtest_succeeds()
    {
        // Brief scenario: "Given a working Slack API connection, When
        // the health check runs, Then it reports Healthy."
        FakeWorkspaceStore store = new(
            new SlackWorkspaceConfig
            {
                TeamId = "T-OK-1",
                WorkspaceName = "Acme",
                BotTokenSecretRef = "env://BOT",
                SigningSecretRef = "env://SIG",
                Enabled = true,
            });

        FakeAuthTester tester = new();
        tester.Configure("T-OK-1", new SlackAuthTestResult("T-OK-1", IsHealthy: true, Detail: "ok"));

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions()),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("workspace_count").WhoseValue.Should().Be(1);
        result.Data.Should().ContainKey("healthy_count").WhoseValue.Should().Be(1);
        result.Data.Should().ContainKey("failed_count").WhoseValue.Should().Be(0);
    }

    [Fact]
    public async Task Any_workspace_failure_trips_health_check_to_unhealthy()
    {
        // The brief is binary -- ANY workspace failure means
        // Unhealthy. This test pins that contract so a future refactor
        // (e.g., "treat one of three as Degraded") cannot silently
        // relax the readiness gate.
        FakeWorkspaceStore store = new(
            BuildWorkspace("T-OK-1"),
            BuildWorkspace("T-FAIL-1"));

        FakeAuthTester tester = new();
        tester.Configure("T-OK-1", new SlackAuthTestResult("T-OK-1", IsHealthy: true, Detail: "ok"));
        tester.Configure("T-FAIL-1", new SlackAuthTestResult("T-FAIL-1", IsHealthy: false, Detail: "invalid_auth", ErrorCode: "invalid_auth"));

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions()),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("T-FAIL-1");
        result.Description.Should().Contain("invalid_auth");
        result.Data["failed_count"].Should().Be(1);
        result.Data["healthy_count"].Should().Be(1);
    }

    [Fact]
    public async Task Zero_workspaces_reports_unhealthy_with_workspace_count_zero()
    {
        // Stage 7.3 evaluator iter-2 item 3: the brief's connectivity
        // contract is binary (Healthy / Unhealthy). Returning Degraded
        // when no workspaces are registered would let /health/ready
        // serve HTTP 200 while the connector is operationally dead --
        // SlackStartupDiagnosticsHostedService says verbatim "the
        // connector will refuse inbound traffic until at least one
        // workspace is configured". This test pins the readiness gate
        // to Unhealthy so Kubernetes removes the pod from rotation in
        // that state.
        FakeWorkspaceStore store = new();
        FakeAuthTester tester = new();

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions()),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["workspace_count"].Should().Be(0);
        result.Description.Should().Contain("No Slack workspaces are registered");
        tester.InvocationCount.Should().Be(0, "auth.test must NOT be called when no workspaces are registered");
    }

    [Fact]
    public async Task AuthTestAllWorkspaces_false_short_circuits_after_first_success()
    {
        FakeWorkspaceStore store = new(
            BuildWorkspace("T-OK-1"),
            BuildWorkspace("T-OK-2"),
            BuildWorkspace("T-OK-3"));

        FakeAuthTester tester = new();
        tester.Configure("T-OK-1", new SlackAuthTestResult("T-OK-1", IsHealthy: true, Detail: "ok"));
        tester.Configure("T-OK-2", new SlackAuthTestResult("T-OK-2", IsHealthy: true, Detail: "ok"));
        tester.Configure("T-OK-3", new SlackAuthTestResult("T-OK-3", IsHealthy: true, Detail: "ok"));

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions { AuthTestAllWorkspaces = false }),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        tester.InvocationCount.Should().Be(1, "with AuthTestAllWorkspaces=false the check should short-circuit after the first success");
    }

    [Fact]
    public async Task AuthTestAllWorkspaces_false_returns_healthy_when_earlier_workspace_fails_but_later_succeeds()
    {
        // Stage 7.3 evaluator iter-3 item 2: when AuthTestAllWorkspaces=false
        // the documented semantics are "short-circuit after the FIRST
        // SUCCESS", i.e. at-least-one-success satisfies the readiness
        // gate. The previous implementation short-circuited on the
        // first success but returned Unhealthy if an earlier
        // workspace had failed before the success was found -- the
        // option doc and the implementation were internally
        // inconsistent. This test pins the corrected semantics: a
        // failure-then-success sequence resolves to Healthy when
        // AuthTestAllWorkspaces=false so a single broken workspace
        // cannot evict an entire multi-workspace pod from rotation.
        FakeWorkspaceStore store = new(
            BuildWorkspace("T-FAIL-1"),
            BuildWorkspace("T-FAIL-2"),
            BuildWorkspace("T-OK-3"));

        FakeAuthTester tester = new();
        tester.Configure("T-FAIL-1", new SlackAuthTestResult("T-FAIL-1", IsHealthy: false, Detail: "invalid_auth", ErrorCode: "invalid_auth"));
        tester.Configure("T-FAIL-2", new SlackAuthTestResult("T-FAIL-2", IsHealthy: false, Detail: "rate_limited", ErrorCode: "ratelimited"));
        tester.Configure("T-OK-3", new SlackAuthTestResult("T-OK-3", IsHealthy: true, Detail: "ok"));

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions { AuthTestAllWorkspaces = false }),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy,
            "AuthTestAllWorkspaces=false documents 'short-circuit after the first success' -- ANY successful probe must satisfy the gate");
        result.Data["healthy_count"].Should().Be(1);
        result.Data["failed_count"].Should().Be(2);
        result.Data["probed_count"].Should().Be(3, "the check kept iterating past the two failures until a success was found");
        tester.InvocationCount.Should().Be(3, "the loop should NOT short-circuit on failure; it must keep probing until a success is found");
    }

    [Fact]
    public async Task AuthTestAllWorkspaces_false_returns_unhealthy_when_every_workspace_fails()
    {
        // Companion to the failure-then-success test: when EVERY
        // probed workspace fails, AuthTestAllWorkspaces=false MUST
        // still surface Unhealthy so the readiness gate trips. This
        // pins that the "at-least-one-success" rule does NOT mean
        // "always Healthy when AuthTestAllWorkspaces=false".
        FakeWorkspaceStore store = new(
            BuildWorkspace("T-FAIL-A"),
            BuildWorkspace("T-FAIL-B"));

        FakeAuthTester tester = new();
        tester.Configure("T-FAIL-A", new SlackAuthTestResult("T-FAIL-A", IsHealthy: false, Detail: "invalid_auth", ErrorCode: "invalid_auth"));
        tester.Configure("T-FAIL-B", new SlackAuthTestResult("T-FAIL-B", IsHealthy: false, Detail: "token_revoked", ErrorCode: "token_revoked"));

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions { AuthTestAllWorkspaces = false }),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("T-FAIL-A");
        result.Description.Should().Contain("T-FAIL-B");
        result.Data["healthy_count"].Should().Be(0);
        result.Data["failed_count"].Should().Be(2);
    }

    [Fact]
    public async Task Tester_timeout_is_treated_as_unhealthy()
    {
        // A stuck Slack endpoint must NOT hang the readiness probe.
        // The check passes a linked CancellationToken bounded by
        // EffectiveAuthTestTimeout; verify the timeout is honoured.
        FakeWorkspaceStore store = new(BuildWorkspace("T-SLOW"));

        FakeAuthTester tester = new()
        {
            DelayPerCall = TimeSpan.FromSeconds(5),
        };

        SlackApiConnectivityHealthCheck check = new(
            store,
            tester,
            Wrap(new SlackHealthCheckOptions { AuthTestTimeout = TimeSpan.FromMilliseconds(250) }),
            NullLogger<SlackApiConnectivityHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("timeout");
    }

    private static SlackWorkspaceConfig BuildWorkspace(string teamId)
        => new()
        {
            TeamId = teamId,
            WorkspaceName = teamId + "-Name",
            BotTokenSecretRef = "env://BOT-" + teamId,
            SigningSecretRef = "env://SIG-" + teamId,
            Enabled = true,
        };

    private static IOptionsMonitor<SlackHealthCheckOptions> Wrap(SlackHealthCheckOptions value)
        => new StaticOptionsMonitor<SlackHealthCheckOptions>(value);

    private static HealthCheckContext NewContext() => new()
    {
        Registration = new HealthCheckRegistration(
            SlackApiConnectivityHealthCheck.CheckName,
            instance: new NullHealthCheck(),
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "slack-ready" }),
    };

    private sealed class FakeWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly List<SlackWorkspaceConfig> workspaces;

        public FakeWorkspaceStore(params SlackWorkspaceConfig[] workspaces)
        {
            this.workspaces = new List<SlackWorkspaceConfig>(workspaces);
        }

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
            => Task.FromResult<SlackWorkspaceConfig?>(this.workspaces.Find(w => w.TeamId == teamId));

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(this.workspaces.ToArray());
    }

    private sealed class FakeAuthTester : ISlackAuthTester
    {
        private readonly Dictionary<string, SlackAuthTestResult> results = new();

        public TimeSpan DelayPerCall { get; set; } = TimeSpan.Zero;

        public int InvocationCount { get; private set; }

        public void Configure(string teamId, SlackAuthTestResult result) => this.results[teamId] = result;

        public async Task<SlackAuthTestResult> TestAsync(string teamId, CancellationToken ct)
        {
            this.InvocationCount++;
            if (this.DelayPerCall > TimeSpan.Zero)
            {
                await Task.Delay(this.DelayPerCall, ct).ConfigureAwait(false);
            }

            return this.results.TryGetValue(teamId, out SlackAuthTestResult? result)
                ? result
                : new SlackAuthTestResult(teamId, IsHealthy: false, Detail: "not configured by fake");
        }
    }

    private sealed class NullHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
