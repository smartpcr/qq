// -----------------------------------------------------------------------
// <copyright file="SlackOutboundQueueDepthHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 7.3 unit tests for
/// <see cref="SlackOutboundQueueDepthHealthCheck"/>. Pins the brief's
/// "default 1000" threshold contract:
/// <list type="bullet">
///   <item><description>Depth at or below the threshold reports
///   <see cref="HealthStatus.Healthy"/>.</description></item>
///   <item><description>Depth above the threshold reports
///   <see cref="HealthStatus.Degraded"/> with a descriptive
///   message naming the depth and threshold.</description></item>
///   <item><description>A custom backend that does NOT implement
///   <see cref="ISlackOutboundQueueDepthProbe"/> defaults to
///   <see cref="HealthStatus.Healthy"/> so the readiness signal is
///   not spuriously degraded.</description></item>
/// </list>
/// </summary>
public sealed class SlackOutboundQueueDepthHealthCheckTests
{
    [Fact]
    public async Task Depth_under_threshold_reports_healthy()
    {
        FakeOutboundQueue queue = new() { Depth = 500 };

        SlackOutboundQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions()),
            NullLogger<SlackOutboundQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["depth"].Should().Be(500);
        result.Data["threshold"].Should().Be(1000);
    }

    [Fact]
    public async Task Depth_over_threshold_reports_degraded()
    {
        FakeOutboundQueue queue = new() { Depth = 1500 };

        SlackOutboundQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions()),
            NullLogger<SlackOutboundQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("1500");
        result.Description.Should().Contain("1000");
        result.Data["depth"].Should().Be(1500);
    }

    [Fact]
    public async Task Depth_exactly_at_threshold_reports_healthy()
    {
        // Brief: "Degraded if queue depth EXCEEDS a configurable
        // threshold". Exactly equal to the threshold MUST be Healthy.
        FakeOutboundQueue queue = new() { Depth = 1000 };

        SlackOutboundQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions()),
            NullLogger<SlackOutboundQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Configurable_threshold_is_honoured()
    {
        FakeOutboundQueue queue = new() { Depth = 60 };

        SlackOutboundQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions { OutboundQueueDegradedThreshold = 50 }),
            NullLogger<SlackOutboundQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["threshold"].Should().Be(50);
    }

    [Fact]
    public async Task Backend_without_depth_probe_reports_healthy_with_null_depth()
    {
        // A queue backend supplied by an external host (e.g., a
        // database-backed implementation) might not implement
        // ISlackOutboundQueueDepthProbe. The check MUST default to
        // Healthy so the readiness signal stays positive.
        OpaqueOutboundQueue queue = new();

        SlackOutboundQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions()),
            NullLogger<SlackOutboundQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["depth"].Should().BeNull();
    }

    private static HealthCheckContext NewContext() => new()
    {
        Registration = new HealthCheckRegistration(
            SlackOutboundQueueDepthHealthCheck.CheckName,
            instance: new NullHealthCheck(),
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "slack-ready" }),
    };

    private sealed class FakeOutboundQueue : ISlackOutboundQueue, ISlackOutboundQueueDepthProbe
    {
        public int Depth { get; set; }

        public int GetCurrentDepth() => this.Depth;

        public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope) => ValueTask.CompletedTask;

        public ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct)
            => ValueTask.FromResult<SlackOutboundEnvelope>(null!);
    }

    private sealed class OpaqueOutboundQueue : ISlackOutboundQueue
    {
        public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope) => ValueTask.CompletedTask;

        public ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct)
            => ValueTask.FromResult<SlackOutboundEnvelope>(null!);
    }

    private sealed class NullHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
