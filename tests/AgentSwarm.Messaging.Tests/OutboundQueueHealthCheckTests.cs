// -----------------------------------------------------------------------
// <copyright file="OutboundQueueHealthCheckTests.cs" company="Microsoft Corp.">
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 6.2 — pins for <see cref="OutboundQueueHealthCheck"/>. The
/// composite check fans in two pressure signals (outbound queue
/// depth + dead-letter queue depth) into a single liveness status.
/// </summary>
public sealed class OutboundQueueHealthCheckTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<MessagingDbContext> _options = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using (var ctx = new MessagingDbContext(_options))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddScoped<MessagingDbContext>(sp =>
            new MessagingDbContext(sp.GetRequiredService<DbContextOptions<MessagingDbContext>>()));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task CheckHealthAsync_WhenAllWithinThresholds_ReturnsHealthy()
    {
        await SeedOutboundAsync(
            (OutboundMessageStatus.Pending, 5),
            (OutboundMessageStatus.Sending, 1));

        var dlq = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().Contain("queue_depth", 6);
        result.Data.Should().Contain("dlq_depth", 0);
        result.Data.Should().Contain("queue_degraded_threshold", 1000);
        result.Data.Should().Contain("dlq_unhealthy_threshold", 100);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenQueueDepthExceedsDegraded_ReturnsDegraded()
    {
        // Brief: "reports degraded if queue depth exceeds 1000".
        // We use depth=3, threshold=2 to keep the seed cheap.
        await SeedOutboundAsync((OutboundMessageStatus.Pending, 3));

        var dlq = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(dlq, degraded: 2, unhealthy: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("3");
        result.Description.Should().Contain("2");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDlqExceedsUnhealthy_ReturnsUnhealthy_EvenIfQueueIsHealthy()
    {
        // DLQ pressure ALWAYS wins — unhealthy is the highest
        // severity and never gets demoted by a healthy queue.
        var dlq = new StubDeadLetterQueue(count: 10);
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 5);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("10");
        result.Description.Should().Contain("5");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDlqExceedsThresholdAndQueueAlsoElevated_ReturnsUnhealthy()
    {
        // The most-severe-wins rule: an elevated queue is normally
        // Degraded, but a simultaneously-elevated DLQ promotes to
        // Unhealthy.
        await SeedOutboundAsync((OutboundMessageStatus.Pending, 3));
        var dlq = new StubDeadLetterQueue(count: 6);
        var check = NewCheck(dlq, degraded: 2, unhealthy: 5);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_OnlyCountsPendingAndSending_ExcludesTerminalRows()
    {
        // The queue depth signal MUST exclude terminal rows. The
        // outbox carries Sent/Failed/DeadLettered history for the
        // operator audit screen; those rows have nothing to do with
        // backpressure.
        await SeedOutboundAsync(
            (OutboundMessageStatus.Pending, 2),
            (OutboundMessageStatus.Sending, 1),
            (OutboundMessageStatus.Sent, 50),
            (OutboundMessageStatus.Failed, 5),
            (OutboundMessageStatus.DeadLettered, 3));

        var dlq = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().Contain("queue_depth", 3);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenQueueProbeThrows_ReturnsUnhealthy_WithException()
    {
        // Drop the outbox to simulate a transient EF/SQLite failure
        // on the queue-depth probe — the brief's "Failed to read
        // outbound queue depth" surface.
        await using (var ctx = new MessagingDbContext(_options))
        {
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE outbox;");
        }

        var dlq = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().NotBeNull();
        result.Description.Should().Contain("outbound queue depth");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDlqProbeThrows_ReturnsUnhealthy_WithException()
    {
        var dlq = new ThrowingDeadLetterQueue(new InvalidOperationException("dlq backing-store unreachable"));
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception!.Message.Should().Contain("dlq backing-store unreachable");
        result.Description.Should().Contain("dead-letter queue depth");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenCallerCancellationTokenIsCancelled_PropagatesOperationCanceledException()
    {
        // Iter-3 evaluator item 2 — the caller's CancellationToken
        // cancelling MUST propagate as OperationCanceledException
        // so the HealthCheckService can treat request abort / host
        // shutdown as the out-of-band signal it is. The earlier
        // implementation caught Exception broadly, which lied about
        // outbound-queue health on every aborted /healthz request.
        //
        // We pre-cancel the token; EF Core's CountAsync observes
        // the token via ThrowIfCancellationRequested and throws
        // a TaskCanceledException (an OCE subclass).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var dlq = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        Func<Task> act = () => check.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation must propagate so HealthCheckService can handle host-shutdown / request-abort natively rather than misreport a queue outage");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenDlqProbeThrowsOperationCanceled_PropagatesRatherThanReturnsUnhealthy()
    {
        // Iter-3 evaluator item 2 — even when the OCE originates from
        // INSIDE the DLQ adapter (rather than from the caller's CT),
        // the OCE-propagation contract holds. The catch clause is
        // narrowed with `when (ex is not OperationCanceledException)`
        // so OCE bypasses the Unhealthy-wrapper completely.
        var dlq = new ThrowingDeadLetterQueue(new OperationCanceledException("simulated cancellation from DLQ adapter"));
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        Func<Task> act = () => check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "OCE from the DLQ probe MUST NOT be converted to an Unhealthy result — that would mask the fact that the operation was cancelled");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenQueueProbeThrowsNonOceException_ReturnsUnhealthy()
    {
        // Pin the contract that a NON-OCE exception still surfaces
        // as Unhealthy (the original behaviour we want to keep) —
        // this guards against a regression that would swallow real
        // database faults along with OCE.
        await using (var ctx = new MessagingDbContext(_options))
        {
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE outbox;");
        }

        var dlq = new StubDeadLetterQueue(count: 0);
        var check = NewCheck(dlq, degraded: 1000, unhealthy: 100);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "non-OCE exceptions from the queue probe must still report Unhealthy — only OCE is special-cased");
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public void Name_Constant_StableForRegistration()
    {
        OutboundQueueHealthCheck.Name.Should().Be("outbound_queue");
    }

    [Fact]
    public void Constructor_NullScopeFactory_Throws()
    {
        Action act = () => new OutboundQueueHealthCheck(
            null!,
            new StubDeadLetterQueue(0),
            Options.Create(new OutboundQueueOptions()),
            Options.Create(new DeadLetterQueueOptions()),
            NullLogger<OutboundQueueHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullDeadLetterQueue_Throws()
    {
        Action act = () => new OutboundQueueHealthCheck(
            _services.GetRequiredService<IServiceScopeFactory>(),
            null!,
            Options.Create(new OutboundQueueOptions()),
            Options.Create(new DeadLetterQueueOptions()),
            NullLogger<OutboundQueueHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullQueueOptions_Throws()
    {
        Action act = () => new OutboundQueueHealthCheck(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new StubDeadLetterQueue(0),
            null!,
            Options.Create(new DeadLetterQueueOptions()),
            NullLogger<OutboundQueueHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullDlqOptions_Throws()
    {
        Action act = () => new OutboundQueueHealthCheck(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new StubDeadLetterQueue(0),
            Options.Create(new OutboundQueueOptions()),
            null!,
            NullLogger<OutboundQueueHealthCheck>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DegradedDepthThreshold_DefaultsToOneThousand_PerBrief()
    {
        // Stage 6.2 brief: "reports degraded if queue depth exceeds
        // 1000". The default on the options class is the contract.
        new OutboundQueueOptions().DegradedDepthThreshold.Should().Be(1000);
    }

    private OutboundQueueHealthCheck NewCheck(IDeadLetterQueue dlq, int degraded, int unhealthy)
    {
        return new OutboundQueueHealthCheck(
            _services.GetRequiredService<IServiceScopeFactory>(),
            dlq,
            Options.Create(new OutboundQueueOptions { DegradedDepthThreshold = degraded }),
            Options.Create(new DeadLetterQueueOptions { UnhealthyThreshold = unhealthy }),
            NullLogger<OutboundQueueHealthCheck>.Instance);
    }

    private async Task SeedOutboundAsync(params (OutboundMessageStatus status, int count)[] rows)
    {
        await using var ctx = new MessagingDbContext(_options);
        var now = DateTimeOffset.UtcNow;
        foreach (var (status, count) in rows)
        {
            for (var i = 0; i < count; i++)
            {
                ctx.OutboundMessages.Add(new OutboundMessage
                {
                    MessageId = Guid.NewGuid(),
                    IdempotencyKey = $"idem-{status}-{i}-{Guid.NewGuid():N}",
                    ChatId = 1234L,
                    SourceType = OutboundSourceType.StatusUpdate,
                    Payload = "body",
                    Severity = MessageSeverity.Normal,
                    CorrelationId = "trace-" + Guid.NewGuid().ToString("N"),
                    CreatedAt = now,
                    Status = status,
                });
            }
        }
        await ctx.SaveChangesAsync();
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

        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(_count);

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

        public Task<int> CountAsync(CancellationToken ct) => Task.FromException<int>(_ex);

        public Task MarkAlertSentAsync(Guid originalMessageId, DateTimeOffset alertSentAt, CancellationToken ct)
            => Task.FromException(_ex);
    }
}
