// -----------------------------------------------------------------------
// <copyright file="SlackHealthCheckOptionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System;
using AgentSwarm.Messaging.Slack.Diagnostics;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 7.3 contract tests pinning the default thresholds and endpoint
/// paths called out by the implementation-plan brief: outbound queue
/// degraded at &gt; 1000, DLQ unhealthy at &gt; 100, readiness probe at
/// <c>/health/ready</c> and liveness probe at <c>/health/live</c>. A
/// rename or drift on any of these defaults silently breaks the
/// Kubernetes manifests that consume the probes, so the assertions
/// here are intentionally narrow and literal.
/// </summary>
public sealed class SlackHealthCheckOptionsTests
{
    [Fact]
    public void Default_OutboundQueueDegradedThreshold_matches_brief()
    {
        SlackHealthCheckOptions opts = new();
        opts.OutboundQueueDegradedThreshold.Should().Be(
            1000,
            "implementation-plan.md Stage 7.3 step 2 pins the default outbound-queue Degraded threshold at 1000");
    }

    [Fact]
    public void Default_DeadLetterUnhealthyThreshold_matches_brief()
    {
        SlackHealthCheckOptions opts = new();
        opts.DeadLetterUnhealthyThreshold.Should().Be(
            100,
            "implementation-plan.md Stage 7.3 step 3 pins the default DLQ Unhealthy threshold at 100");
    }

    [Fact]
    public void Default_ready_and_live_paths_match_kubernetes_convention()
    {
        SlackHealthCheckOptions opts = new();
        opts.ReadyEndpointPath.Should().Be(
            "/health/ready",
            "implementation-plan.md Stage 7.3 step 4 pins the readiness probe path at /health/ready");
        opts.LiveEndpointPath.Should().Be(
            "/health/live",
            "implementation-plan.md Stage 7.3 step 4 pins the liveness probe path at /health/live");
    }

    [Fact]
    public void Default_AuthTestAllWorkspaces_is_true()
    {
        SlackHealthCheckOptions opts = new();
        opts.AuthTestAllWorkspaces.Should().BeTrue(
            "the connectivity check probes every enabled workspace by default so a single mis-configured workspace surfaces");
    }

    [Fact]
    public void SectionName_is_Slack_Health()
    {
        SlackHealthCheckOptions.SectionName.Should().Be(
            "Slack:Health",
            "AddSlackHealthChecks binds from Slack:Health; renaming the constant breaks operator-supplied appsettings overrides");
    }

    [Fact]
    public void EffectiveOutboundDegradedThreshold_clamps_non_positive_to_one()
    {
        SlackHealthCheckOptions opts = new() { OutboundQueueDegradedThreshold = 0 };
        opts.EffectiveOutboundDegradedThreshold.Should().Be(
            1,
            "the consumed threshold MUST be > 0 so a mis-configured zero does not permanently degrade readiness");

        opts.OutboundQueueDegradedThreshold = -50;
        opts.EffectiveOutboundDegradedThreshold.Should().Be(1);
    }

    [Fact]
    public void EffectiveDeadLetterUnhealthyThreshold_clamps_non_positive_to_one()
    {
        SlackHealthCheckOptions opts = new() { DeadLetterUnhealthyThreshold = 0 };
        opts.EffectiveDeadLetterUnhealthyThreshold.Should().Be(1);

        opts.DeadLetterUnhealthyThreshold = -10;
        opts.EffectiveDeadLetterUnhealthyThreshold.Should().Be(1);
    }

    [Fact]
    public void EffectiveAuthTestTimeout_clamps_short_values_to_minimum()
    {
        SlackHealthCheckOptions opts = new() { AuthTestTimeout = TimeSpan.FromMilliseconds(50) };
        opts.EffectiveAuthTestTimeout.Should().Be(
            TimeSpan.FromMilliseconds(250),
            "the consumed timeout must give SlackNet enough budget to dispatch; clamp protects against typos");
    }
}
