// -----------------------------------------------------------------------
// <copyright file="SlackDeadLetterQueueDepthHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Queues;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 7.3 unit tests for
/// <see cref="SlackDeadLetterQueueDepthHealthCheck"/>. Pins the brief
/// scenario verbatim: "Given DLQ depth at 150 (threshold 100), When
/// the health check runs, Then it reports <c>Unhealthy</c> with a
/// descriptive message."
/// </summary>
public sealed class SlackDeadLetterQueueDepthHealthCheckTests
{
    [Fact]
    public async Task Depth_under_threshold_reports_healthy()
    {
        FakeDeadLetterQueue queue = new() { Depth = 50 };

        SlackDeadLetterQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions()),
            NullLogger<SlackDeadLetterQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["depth"].Should().Be(50);
        result.Data["threshold"].Should().Be(100);
    }

    [Fact]
    public async Task Brief_scenario_depth_150_threshold_100_reports_unhealthy_with_descriptive_message()
    {
        // Brief scenario: "Given DLQ depth at 150 (threshold 100),
        // When the health check runs, Then it reports Unhealthy
        // with a descriptive message."
        FakeDeadLetterQueue queue = new() { Depth = 150 };

        SlackDeadLetterQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions { DeadLetterUnhealthyThreshold = 100 }),
            NullLogger<SlackDeadLetterQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotBeNullOrWhiteSpace(
            "the brief explicitly calls for a descriptive message so operators can triage from the health-check JSON");
        result.Description.Should().Contain("150", "the descriptive message includes the actual depth");
        result.Description.Should().Contain("100", "the descriptive message includes the configured threshold");
        result.Data["depth"].Should().Be(150);
        result.Data["threshold"].Should().Be(100);
    }

    [Fact]
    public async Task Depth_exactly_at_threshold_reports_healthy()
    {
        // Brief: "Unhealthy if DLQ depth EXCEEDS a configurable
        // threshold". Equal to the threshold MUST be Healthy.
        FakeDeadLetterQueue queue = new() { Depth = 100 };

        SlackDeadLetterQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions()),
            NullLogger<SlackDeadLetterQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Backend_without_depth_probe_falls_back_to_InspectAsync()
    {
        // A queue backend that does NOT implement the cheap depth
        // probe still produces a correct health signal via a single
        // InspectAsync call. The brief defaults all production
        // backends to the cheap path, but the fallback is required
        // by the public ISlackDeadLetterQueue contract.
        OpaqueDeadLetterQueue queue = new();
        for (int i = 0; i < 200; i++)
        {
            await queue.EnqueueAsync(BuildEntry($"E{i}"));
        }

        SlackDeadLetterQueueDepthHealthCheck check = new(
            queue,
            new StaticOptionsMonitor<SlackHealthCheckOptions>(new SlackHealthCheckOptions { DeadLetterUnhealthyThreshold = 100 }),
            NullLogger<SlackDeadLetterQueueDepthHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(NewContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["depth"].Should().Be(200);
    }

    private static SlackDeadLetterEntry BuildEntry(string id) => new()
    {
        // EntryId is Guid (per SlackDeadLetterEntry's required init);
        // generate a fresh one per call -- only the count is asserted by
        // these tests so the value is irrelevant beyond being unique.
        EntryId = System.Guid.NewGuid(),
        Source = SlackDeadLetterSource.Outbound,
        Reason = "test",
        ExceptionType = "TestException",
        AttemptCount = 5,
        FirstFailedAt = System.DateTimeOffset.UtcNow,
        DeadLetteredAt = System.DateTimeOffset.UtcNow,
        CorrelationId = "corr-" + id,

        // SlackOutboundEnvelope is a positional record. Stage 1.3 pins
        // the constructor shape as
        // (TaskId, CorrelationId, MessageType, BlockKitPayload, ThreadTs)
        // where MessageType is a SlackOutboundOperationKind (the brief's
        // "MessageType" field name is preserved verbatim, see the type's
        // XML doc). Object-initializer syntax would only work for the
        // optional `MessageTs` / `ViewId` / `EnvelopeId` init-only
        // members, not for the required positional parameters.
        Payload = new AgentSwarm.Messaging.Slack.Transport.SlackOutboundEnvelope(
            TaskId: "task-" + id,
            CorrelationId: "corr-" + id,
            MessageType: AgentSwarm.Messaging.Slack.Transport.SlackOutboundOperationKind.PostMessage,
            BlockKitPayload: "{}",
            ThreadTs: null),
    };

    private static HealthCheckContext NewContext() => new()
    {
        Registration = new HealthCheckRegistration(
            SlackDeadLetterQueueDepthHealthCheck.CheckName,
            instance: new NullHealthCheck(),
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "slack-ready" }),
    };

    private sealed class FakeDeadLetterQueue : ISlackDeadLetterQueue, ISlackDeadLetterQueueDepthProbe
    {
        public int Depth { get; set; }

        public int GetCurrentDepth() => this.Depth;

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(System.Array.Empty<SlackDeadLetterEntry>());
    }

    private sealed class OpaqueDeadLetterQueue : ISlackDeadLetterQueue
    {
        private readonly List<SlackDeadLetterEntry> entries = new();

        public ValueTask EnqueueAsync(SlackDeadLetterEntry entry, CancellationToken ct = default)
        {
            this.entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SlackDeadLetterEntry>> InspectAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SlackDeadLetterEntry>>(this.entries.ToArray());
    }

    private sealed class NullHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
