// -----------------------------------------------------------------------
// <copyright file="DeadLetterQueueHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 4.2 — pins <see cref="DeadLetterQueueHealthCheck"/> against
/// the brief's scenario: "Given 10 messages in the dead-letter queue
/// and threshold is 5, When health check is queried, Then status is
/// Unhealthy." Also covers boundary, underflow, exception-propagation
/// and the constructor null-guards.
/// </summary>
public sealed class DeadLetterQueueHealthCheckTests
{
    private static DeadLetterQueueHealthCheck NewCheck(IDeadLetterQueue queue, int threshold)
    {
        var options = Options.Create(new DeadLetterQueueOptions { UnhealthyThreshold = threshold });
        return new DeadLetterQueueHealthCheck(queue, options);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDepthExceedsThreshold_ReturnsUnhealthy()
    {
        // Brief scenario: "Given 10 messages in the dead-letter queue
        // and threshold is 5, When health check is queried, Then
        // status is Unhealthy."
        var queue = new StubDeadLetterQueue(count: 10);
        var check = NewCheck(queue, threshold: 5);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Unhealthy,
            "the brief's scenario pins this status for count > threshold");
        result.Data.Should().Contain("count", 10);
        result.Data.Should().Contain("threshold", 5);
        result.Description.Should().Contain("10");
        result.Description.Should().Contain("5");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDepthEqualsThreshold_ReturnsHealthy()
    {
        // Threshold is "exceeds", not "reaches" — equality must NOT
        // trip the unhealthy state so the on-call operator only
        // pages when the queue genuinely outgrew the operator's
        // tolerance. Matches the strict ">" in the implementation.
        var queue = new StubDeadLetterQueue(count: 5);
        var check = NewCheck(queue, threshold: 5);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Healthy,
            "the contract is 'count > threshold → Unhealthy'; at-threshold rows are still within tolerance");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDepthIsZero_ReturnsHealthy()
    {
        var queue = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(queue, threshold: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Healthy,
            "an empty dead-letter queue is the canonical healthy state");
        result.Data.Should().Contain("count", 0);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenCountAsyncThrows_ReturnsUnhealthy_WithException()
    {
        // Operator runbook contract: a DB outage MUST surface as
        // Unhealthy with the exception attached, NOT a confusing
        // "Healthy" because the count probe silently failed.
        var queue = new ThrowingDeadLetterQueue(new InvalidOperationException("db unreachable"));
        var check = NewCheck(queue, threshold: 10);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Unhealthy,
            "a failed depth probe means the operator runbook MUST see Unhealthy + the exception, not a silent zero");
        result.Exception.Should().NotBeNull();
        result.Exception!.Message.Should().Contain("db unreachable");
        result.Data.Should().Contain("threshold", 10);
    }

    [Fact]
    public void Constructor_NullQueue_Throws()
    {
        Action act = () => new DeadLetterQueueHealthCheck(
            null!,
            Options.Create(new DeadLetterQueueOptions()));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Action act = () => new DeadLetterQueueHealthCheck(
            new StubDeadLetterQueue(0),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Name_Constant_StableForRegistration()
    {
        // The worker's AddCheck<DeadLetterQueueHealthCheck>(Name, ...)
        // call relies on this constant; renaming it silently would
        // break the operator's /healthz pivot on the named check.
        DeadLetterQueueHealthCheck.Name.Should().Be("outbound_dead_letter_queue_depth");
    }

    private sealed class StubDeadLetterQueue : IDeadLetterQueue
    {
        private readonly int _count;

        public StubDeadLetterQueue(int count)
        {
            _count = count;
        }

        public Task SendToDeadLetterAsync(OutboundMessage message, FailureReason reason, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeadLetterMessage>>(Array.Empty<DeadLetterMessage>());

        public Task<int> CountAsync(CancellationToken ct)
            => Task.FromResult(_count);

        public Task MarkAlertSentAsync(Guid originalMessageId, DateTimeOffset alertSentAt, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ThrowingDeadLetterQueue : IDeadLetterQueue
    {
        private readonly Exception _ex;

        public ThrowingDeadLetterQueue(Exception ex)
        {
            _ex = ex;
        }

        public Task SendToDeadLetterAsync(OutboundMessage message, FailureReason reason, CancellationToken ct)
            => Task.FromException(_ex);

        public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyList<DeadLetterMessage>>(_ex);

        public Task<int> CountAsync(CancellationToken ct)
            => Task.FromException<int>(_ex);

        public Task MarkAlertSentAsync(Guid originalMessageId, DateTimeOffset alertSentAt, CancellationToken ct)
            => Task.FromException(_ex);
    }
}
