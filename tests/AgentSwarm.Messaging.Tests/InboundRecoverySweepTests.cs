using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram.Webhook;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="InboundRecoverySweep"/> against the
/// implementation-plan.md §195-201 scenarios it owns: failed update
/// replayed; permanently failing update excluded; sweep does not touch
/// Processing rows owned by a live dispatcher; sweep does not touch
/// Completed rows.
/// </summary>
public sealed class InboundRecoverySweepTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private Mock<ITelegramUpdatePipeline> _pipelineMock = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        _pipelineMock = new Mock<ITelegramUpdatePipeline>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();
        services.AddScoped<InboundUpdateProcessor>();
        services.AddSingleton(_pipelineMock.Object);
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task PersistAsync(InboundUpdate row)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
        await store.PersistAsync(row, CancellationToken.None);
    }

    private async Task<IInboundUpdateStore> NewScopedStore()
    {
        await Task.CompletedTask;
        var scope = _services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
    }

    private InboundRecoverySweep NewSweep(int maxRetries = 3, TimeSpan? staleProcessingThreshold = null) =>
        new(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InboundRecoverySweep>.Instance,
            TimeSpan.FromMinutes(1),  // Interval irrelevant for SweepOnceAsync.
            maxRetries,
            staleProcessingThreshold ?? TimeSpan.FromMinutes(30));

    private static InboundUpdate NewRow(long updateId, IdempotencyStatus status = IdempotencyStatus.Received) =>
        new()
        {
            UpdateId = updateId,
            RawPayload = "{\"update_id\":" + updateId
                + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},"
                + "\"from\":{\"id\":2,\"is_bot\":false,\"first_name\":\"u\"},\"text\":\"/status\"}}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = status,
        };

    [Fact]
    public async Task SweepOnce_ReplaysFailedRow_TransitionsToCompleted()
    {
        await PersistAsync(NewRow(101));
        var firstStore = await NewScopedStore();
        await firstStore.MarkFailedAsync(101, "transient downstream", CancellationToken.None);

        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                CorrelationId = "trace-sweep",
            });

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1);
        var store = await NewScopedStore();
        var row = await store.GetByUpdateIdAsync(101, CancellationToken.None);
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
    }

    [Fact]
    public async Task SweepOnce_ExcludesRowsAtMaxRetries()
    {
        await PersistAsync(NewRow(202));
        var store = await NewScopedStore();
        // Drive AttemptCount up to MaxRetries=3.
        for (var i = 0; i < 3; i++)
        {
            await store.MarkFailedAsync(202, "attempt " + i, CancellationToken.None);
        }

        var sweep = NewSweep(maxRetries: 3);
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(0);
        _pipelineMock.Verify(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "exhausted rows must not enter the pipeline; they surface via GetExhaustedAsync for per-row alerting");

        var roundTrip = await store.GetByUpdateIdAsync(202, CancellationToken.None);
        roundTrip!.IdempotencyStatus.Should().Be(IdempotencyStatus.Failed);
        roundTrip.AttemptCount.Should().Be(3);
    }

    [Fact]
    public async Task SweepOnce_DoesNotReclaimProcessingRows()
    {
        // Architecture.md §4.8 says GetRecoverableAsync returns rows in
        // Received, Processing, or Failed state. The sweep iterates them
        // BUT the InboundUpdateProcessor's TryMarkProcessingAsync CAS
        // rejects rows already in Processing — so the pipeline is never
        // invoked for a row owned by a live worker. This is the safety
        // contract: surfacing Processing in GetRecoverableAsync is
        // necessary for crash recovery and per-row alerting; the CAS
        // prevents double execution.
        await PersistAsync(NewRow(303));
        var store = await NewScopedStore();
        await store.TryMarkProcessingAsync(303, CancellationToken.None);

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        _pipelineMock.Verify(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "TryMarkProcessing CAS must reject reclaiming Processing rows from a live worker");

        // iter-5 evaluator item 4 — sweep return must not over-report.
        // The processor returns false when CAS rejects, and the sweep
        // increments `processed` only on true.
        processed.Should().Be(0,
            "sweep must NOT count a CAS-rejected row as processed (iter-5 item 4) — the row was claimed by a live worker, not by the sweep");

        // Row remains in Processing (live owner still holds it).
        var roundTrip = await store.GetByUpdateIdAsync(303, CancellationToken.None);
        roundTrip!.IdempotencyStatus.Should().Be(IdempotencyStatus.Processing);
    }

    [Fact]
    public async Task SweepOnce_ReplaysReceivedRows_LeftBehindByEnqueueFailure()
    {
        // The webhook endpoint persists Received BEFORE enqueueing on
        // the in-memory channel. If the process crashes between Persist
        // and the dispatcher draining the row, the row stays Received
        // and the next sweep must pick it up.
        await PersistAsync(NewRow(404));

        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                CorrelationId = "trace-sweep",
            });

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1);
        var store = await NewScopedStore();
        (await store.GetByUpdateIdAsync(404, CancellationToken.None))!
            .IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
    }

    [Fact]
    public async Task SweepOnce_HandlerThrows_LeavesRowFailed_WithIncrementedAttemptCount()
    {
        await PersistAsync(NewRow(505));

        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still failing"));

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1);  // The pipeline call still ran (and threw).
        var store = await NewScopedStore();
        var row = await store.GetByUpdateIdAsync(505, CancellationToken.None);
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Failed);
        row.AttemptCount.Should().Be(1);
        row.ErrorDetail.Should().Contain("still failing");
    }

    [Fact]
    public async Task SweepOnce_NoEligibleRows_ReturnsZero_WithoutInvokingPipeline()
    {
        var sweep = NewSweep();

        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(0);
        _pipelineMock.Verify(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Ctor_ValidatesArguments()
    {
        var factory = _services.GetRequiredService<IServiceScopeFactory>();

        FluentActions
            .Invoking(() => new InboundRecoverySweep(null!, NullLogger<InboundRecoverySweep>.Instance, TimeSpan.FromSeconds(1), 3))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new InboundRecoverySweep(factory, null!, TimeSpan.FromSeconds(1), 3))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new InboundRecoverySweep(factory, NullLogger<InboundRecoverySweep>.Instance, TimeSpan.Zero, 3))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() => new InboundRecoverySweep(factory, NullLogger<InboundRecoverySweep>.Instance, TimeSpan.FromSeconds(1), 0))
            .Should().Throw<ArgumentOutOfRangeException>();
        // Iter-5 evaluator item 3 — the 5-arg ctor's staleProcessingThreshold
        // must reject non-positive values just like sweepInterval.
        FluentActions
            .Invoking(() => new InboundRecoverySweep(factory, NullLogger<InboundRecoverySweep>.Instance, TimeSpan.FromSeconds(1), 3, TimeSpan.Zero))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() => new InboundRecoverySweep(factory, NullLogger<InboundRecoverySweep>.Instance, TimeSpan.FromSeconds(1), 3, TimeSpan.FromMinutes(-1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    // ============================================================
    // Iteration-3 evaluator feedback item 3 — sweep MUST emit the
    // inbound_update_exhausted_retries metric every tick, even when
    // GetRecoverableAsync returned zero rows. The signal is logged
    // at Warning level when count > 0 and routed to an
    // IAlertService when one is registered in DI.
    // ============================================================

    [Fact]
    public async Task SweepOnce_WithExhaustedRows_LogsWarningWithMetricConstant()
    {
        // Drive one row past MaxRetries=2 so it qualifies as exhausted.
        await PersistAsync(NewRow(801));
        var store = await NewScopedStore();
        await store.MarkFailedAsync(801, "attempt-1", CancellationToken.None);
        await store.MarkFailedAsync(801, "attempt-2", CancellationToken.None);

        var capture = new CapturingLoggerProvider();
        var sweep = NewSweepWithLogger(capture, maxRetries: 2);

        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(0, "an exhausted row is excluded from recoverable, so the sweep does not invoke the pipeline for it");
        capture.Lines.Should()
            .Contain(l => l.Level == LogLevel.Warning
                       && l.Message.Contains(InboundRecoverySweep.ExhaustedRetriesMetric)
                       && l.Message.Contains("ExhaustedRetryCount=1"),
                "the sweep must emit inbound_update_exhausted_retries at Warning when count > 0 — operators rely on this metric to triage dead-lettered inbound updates");
    }

    [Fact]
    public async Task SweepOnce_WithExhaustedRows_LogsPerRowErrorWithUpdateIdAndErrorDetail()
    {
        // iter-4 evaluator item 6 — implementation-plan.md §188/§201
        // requires the per-row Error log line to carry UpdateId and
        // ErrorDetail so an operator tailing logs can identify the
        // specific stranded updates without joining against the DB.
        await PersistAsync(NewRow(7701) with { CorrelationId = "trace-7701" });
        await PersistAsync(NewRow(7702));
        var store = await NewScopedStore();
        await store.MarkFailedAsync(7701, "downstream-503", CancellationToken.None);
        await store.MarkFailedAsync(7701, "downstream-503-still", CancellationToken.None);
        await store.MarkFailedAsync(7702, "deserializer-blew-up", CancellationToken.None);
        await store.MarkFailedAsync(7702, "deserializer-blew-up", CancellationToken.None);

        var capture = new CapturingLoggerProvider();
        var sweep = NewSweepWithLogger(capture, maxRetries: 2);

        await sweep.SweepOnceAsync(CancellationToken.None);

        capture.Lines.Should()
            .Contain(l => l.Level == LogLevel.Error
                       && l.Message.Contains(InboundRecoverySweep.ExhaustedRetriesMetric)
                       && l.Message.Contains("UpdateId=7701")
                       && l.Message.Contains("downstream-503-still")
                       && l.Message.Contains("trace-7701"),
                "row 7701 must be logged with its UpdateId, ErrorDetail, and CorrelationId at Error level (implementation-plan §188/§201)");
        capture.Lines.Should()
            .Contain(l => l.Level == LogLevel.Error
                       && l.Message.Contains(InboundRecoverySweep.ExhaustedRetriesMetric)
                       && l.Message.Contains("UpdateId=7702")
                       && l.Message.Contains("deserializer-blew-up"),
                "row 7702 must also be logged with its UpdateId and ErrorDetail (per-row, not aggregate)");
    }

    [Fact]
    public async Task SweepOnce_WithZeroExhausted_StillEmitsHeartbeatAtInformation()
    {
        // Empty database; recoverable=0 AND exhausted=0.
        var capture = new CapturingLoggerProvider();
        var sweep = NewSweepWithLogger(capture);

        await sweep.SweepOnceAsync(CancellationToken.None);

        capture.Lines.Should()
            .Contain(l => l.Level == LogLevel.Information
                       && l.Message.Contains(InboundRecoverySweep.ExhaustedRetriesMetric)
                       && l.Message.Contains("ExhaustedRetryCount=0"),
                "an Information heartbeat on every tick gives operators a liveness signal even when there is nothing to alert on — silence is indistinguishable from a stuck sweep");
    }

    [Fact]
    public async Task SweepOnce_WithExhaustedRows_InvokesRegisteredAlertService()
    {
        // Re-build the services container with an IAlertService registered.
        await _services.DisposeAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();
        services.AddScoped<InboundUpdateProcessor>();
        services.AddSingleton(_pipelineMock.Object);
        var alertMock = new Mock<IAlertService>();
        alertMock.Setup(a => a.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        services.AddSingleton(alertMock.Object);
        _services = services.BuildServiceProvider();

        await PersistAsync(NewRow(802));
        var store = await NewScopedStore();
        await store.MarkFailedAsync(802, "a", CancellationToken.None);
        await store.MarkFailedAsync(802, "b", CancellationToken.None);

        var sweep = NewSweep(maxRetries: 2);
        await sweep.SweepOnceAsync(CancellationToken.None);

        alertMock.Verify(
            a => a.SendAlertAsync(
                It.Is<string>(s => s.Contains("exhausted retries")),
                It.Is<string>(d => d.Contains(InboundRecoverySweep.ExhaustedRetriesMetric)
                                 && d.Contains("Count=1")),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "when an IAlertService is registered the sweep must route the exhausted-retry signal out-of-band so operators get paged without relying on the same channel that is dead-lettering");
    }

    [Fact]
    public async Task SweepOnce_AlertServiceThrows_DoesNotFailSweep()
    {
        await _services.DisposeAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();
        services.AddScoped<InboundUpdateProcessor>();
        services.AddSingleton(_pipelineMock.Object);
        var alertMock = new Mock<IAlertService>();
        alertMock.Setup(a => a.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("PagerDuty down"));
        services.AddSingleton(alertMock.Object);
        _services = services.BuildServiceProvider();

        await PersistAsync(NewRow(803));
        var store = await NewScopedStore();
        await store.MarkFailedAsync(803, "a", CancellationToken.None);
        await store.MarkFailedAsync(803, "b", CancellationToken.None);

        var sweep = NewSweep(maxRetries: 2);

        var act = async () => await sweep.SweepOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a misbehaving alert sink must not block the sweep — the warning log line is still authoritative and operators will see it");
    }

    // ============================================================
    // Iteration-3 evaluator feedback item 1 — sweep replay MUST
    // reuse the original InboundUpdate.CorrelationId, NOT invent
    // a synthetic sweep-<id> identifier. Tests below verify the
    // MessengerEvent flowing into the pipeline carries the
    // persisted correlation id.
    // ============================================================

    [Fact]
    public async Task SweepOnce_ReplaysRow_PropagatesPersistedCorrelationIdToPipeline()
    {
        const long updateId = 9100;
        const string originalCorrelation = "trace-from-webhook-9100";
        await PersistAsync(NewRow(updateId) with { CorrelationId = originalCorrelation });

        MessengerEvent? captured = null;
        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MessengerEvent, CancellationToken>((evt, _) => captured = evt)
            .ReturnsAsync(new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                CorrelationId = originalCorrelation,
            });

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1);
        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(originalCorrelation,
            "the sweep must reuse the persisted correlation id rather than synthesising sweep-<id> — the outbound reply must share the inbound trace per the story's correlation requirement");
    }

    [Fact]
    public void ResolveCorrelationId_UsesPersistedValue_WhenSet()
    {
        var row = new InboundUpdate
        {
            UpdateId = 1,
            RawPayload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = IdempotencyStatus.Received,
            CorrelationId = "persisted-trace-xyz",
        };

        InboundRecoverySweep.ResolveCorrelationId(row).Should().Be("persisted-trace-xyz");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveCorrelationId_FallsBackToSynthetic_WhenBlank(string? blank)
    {
        var row = new InboundUpdate
        {
            UpdateId = 12345,
            RawPayload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = IdempotencyStatus.Received,
            CorrelationId = blank,
        };

        InboundRecoverySweep.ResolveCorrelationId(row).Should().Be("sweep-12345",
            "legacy rows persisted before the CorrelationId column existed (or hand-seeded test rows) must still flow through the processor, which rejects blank correlation ids");
    }

    // ============================================================
    // Iter-5 evaluator feedback item 3 — the sweep's metric/alert
    // count MUST come from GetExhaustedRetryCountAsync (true total),
    // NOT from the per-row sample which is capped at
    // ExhaustedRowsPerTickLimit. The per-row Error log lines remain
    // capped, but the count must be honest.
    // ============================================================

    [Fact]
    public async Task SweepOnce_TotalCount_ComesFromGetExhaustedRetryCountAsync_NotSample()
    {
        // Stage a number of exhausted rows that EXCEEDS the per-tick
        // sample cap so we can detect whether the warning line uses
        // the true total or the (smaller) sampled count. The
        // ExhaustedRowsPerTickLimit is 50; we stage 53 rows.
        const int exhaustedTotal = 53;
        var capture = new CapturingLoggerProvider();
        var sweep = NewSweepWithLogger(capture, maxRetries: 1);

        for (var i = 0; i < exhaustedTotal; i++)
        {
            var id = 100_000L + i;
            await PersistAsync(NewRow(id));
            await using var scope = _services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
            await store.MarkFailedAsync(id, "boom-" + i, CancellationToken.None);
        }

        await sweep.SweepOnceAsync(CancellationToken.None);

        capture.Lines.Should()
            .Contain(l => l.Level == LogLevel.Warning
                       && l.Message.Contains(InboundRecoverySweep.ExhaustedRetriesMetric)
                       && l.Message.Contains("ExhaustedRetryCount=" + exhaustedTotal),
                "the warning's ExhaustedRetryCount MUST be the true total (" + exhaustedTotal +
                ") sourced from GetExhaustedRetryCountAsync — not the per-row sample size capped at " +
                InboundRecoverySweep.ExhaustedRowsPerTickLimit + " (iter-5 item 3)");
        capture.Lines.Should()
            .Contain(l => l.Level == LogLevel.Warning
                       && l.Message.Contains("SampledRowCount=" + InboundRecoverySweep.ExhaustedRowsPerTickLimit)
                       && l.Message.Contains("remaining 3 row(s)"),
                "when the sample is smaller than the true total, the warning must say so explicitly so the operator knows there are more rows to triage by direct DB query");
    }

    [Fact]
    public async Task SweepOnce_TotalCount_AlertServiceReceivesTrueTotal_NotCappedSample()
    {
        // Same scenario as above, but verifies the alert service
        // receives the TRUE total in the subject and detail body —
        // alerts are the operator's primary signal channel.
        await _services.DisposeAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();
        services.AddScoped<InboundUpdateProcessor>();
        services.AddSingleton(_pipelineMock.Object);
        var alertMock = new Mock<IAlertService>();
        alertMock.Setup(a => a.SendAlertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        services.AddSingleton(alertMock.Object);
        _services = services.BuildServiceProvider();

        const int exhaustedTotal = 53;
        for (var i = 0; i < exhaustedTotal; i++)
        {
            var id = 200_000L + i;
            await PersistAsync(NewRow(id));
            await using var scope = _services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
            await store.MarkFailedAsync(id, "boom-" + i, CancellationToken.None);
        }

        var sweep = NewSweep(maxRetries: 1);
        await sweep.SweepOnceAsync(CancellationToken.None);

        alertMock.Verify(a => a.SendAlertAsync(
                It.Is<string>(s => s.Contains(exhaustedTotal + " row(s) exhausted retries")),
                It.Is<string>(d => d.Contains("Count=" + exhaustedTotal)),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the alert subject AND body must carry the true total (" + exhaustedTotal +
            ") sourced from GetExhaustedRetryCountAsync — not the capped sample size");
    }

    // ============================================================
    // Iter-5 evaluator feedback item 4 — the sweep's `processed`
    // counter must NOT increment when InboundUpdateProcessor.ProcessAsync
    // returns false (CAS rejected by a parallel worker).
    // ============================================================

    [Fact]
    public async Task SweepOnce_OnlyCountsRowsWherePipelineActuallyRan()
    {
        // Stage ONE Received row (the sweep WILL drive it) and ONE
        // Processing row (the sweep observes via GetRecoverable, then
        // the processor's CAS rejects it). The sweep must report
        // exactly 1 processed row.
        await PersistAsync(NewRow(601));
        await PersistAsync(NewRow(602));
        var store = await NewScopedStore();
        await store.TryMarkProcessingAsync(602, CancellationToken.None);

        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                CorrelationId = "trace-601",
            });

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1,
            "the sweep must count ONLY the row whose pipeline actually ran (601). Row 602 was CAS-rejected by the live-worker claim and ProcessAsync returned false (iter-5 item 4)");
        _pipelineMock.Verify(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the pipeline must run exactly once (for row 601); the CAS rejection of 602 must short-circuit BEFORE the pipeline call");
    }

    // ============================================================
    // Iter-5 evaluator feedback item 3 — the periodic sweep MUST
    // reclaim orphaned Processing rows (lease past staleness OR
    // ProcessingStartedAt is null for legacy rows). Without this
    // step a row that became orphaned AFTER startup (crash mid-
    // pipeline, swallowed-release exception) would strand in
    // Processing until the next host restart's one-shot reset.
    // ============================================================

    [Fact]
    public async Task SweepOnce_ReclaimsStaleProcessingRow_AndReplays()
    {
        // Stage a Processing row with a backdated ProcessingStartedAt
        // (1 hour ago) — beyond the sweep's 30-min default staleness.
        // The reclaim step transitions it back to Received, then the
        // sweep's recoverable query picks it up and the pipeline runs.
        await PersistAsync(NewRow(701));
        var store = await NewScopedStore();
        await store.TryMarkProcessingAsync(701, CancellationToken.None);

        // Directly backdate ProcessingStartedAt via EF to simulate an
        // orphaned row that has been Processing for over an hour
        // (crashed worker, swallowed-release, etc).
        await using (var scope = _services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await ctx.InboundUpdates.FirstAsync(x => x.UpdateId == 701);
            var stale = row with { ProcessingStartedAt = DateTimeOffset.UtcNow.AddHours(-1) };
            ctx.Entry(row).CurrentValues.SetValues(stale);
            await ctx.SaveChangesAsync();
        }

        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                CorrelationId = "trace-stale",
            });

        // Use a tight 30-minute default; the row is 1 hour stale so it
        // matches the reclaim predicate.
        var sweep = NewSweep(staleProcessingThreshold: TimeSpan.FromMinutes(30));
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1,
            "the stale Processing row must be reclaimed to Received and then replayed by the recoverable query on the same sweep tick");

        var roundTrip = await store.GetByUpdateIdAsync(701, CancellationToken.None);
        roundTrip!.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
        roundTrip.ProcessingStartedAt.Should().BeNull(
            "MarkCompleted must clear the lease timestamp so the row carries no stale lease forward");
    }

    [Fact]
    public async Task SweepOnce_LegacyProcessingRowWithNullStartedAt_IsReclaimed()
    {
        // Rows persisted BEFORE the ProcessingStartedAt column existed
        // (i.e. legacy hand-seeded rows) have a null lease timestamp.
        // The reclaim predicate must treat null as stale so these
        // rows can recover on the next sweep tick after the column
        // migration runs.
        await PersistAsync(NewRow(702));
        var store = await NewScopedStore();
        await store.TryMarkProcessingAsync(702, CancellationToken.None);

        // Directly null-out ProcessingStartedAt to simulate a legacy
        // row.
        await using (var scope = _services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var row = await ctx.InboundUpdates.FirstAsync(x => x.UpdateId == 702);
            var legacy = row with { ProcessingStartedAt = null };
            ctx.Entry(row).CurrentValues.SetValues(legacy);
            await ctx.SaveChangesAsync();
        }

        _pipelineMock
            .Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineResult
            {
                Handled = true,
                Succeeded = true,
                CorrelationId = "trace-legacy",
            });

        var sweep = NewSweep();
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1,
            "the reclaim predicate must treat ProcessingStartedAt IS NULL as stale so legacy rows are recoverable");

        var roundTrip = await store.GetByUpdateIdAsync(702, CancellationToken.None);
        roundTrip!.IdempotencyStatus.Should().Be(IdempotencyStatus.Completed);
    }

    [Fact]
    public async Task SweepOnce_FreshProcessingRow_NotReclaimed()
    {
        // Re-pin the existing safety contract: TryMarkProcessing stamps
        // ProcessingStartedAt with UtcNow, so a row that was claimed
        // milliseconds ago is NOT eligible for reclaim. This is the
        // counterpart to the stale-reclaim test — a healthy in-flight
        // handler must never be falsely reset.
        await PersistAsync(NewRow(703));
        var store = await NewScopedStore();
        await store.TryMarkProcessingAsync(703, CancellationToken.None);

        // Use a 30-minute staleness threshold (the production default).
        var sweep = NewSweep(staleProcessingThreshold: TimeSpan.FromMinutes(30));
        var processed = await sweep.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(0,
            "a fresh claim must not be reclaimed — its ProcessingStartedAt is well within the 30-min staleness window");

        var roundTrip = await store.GetByUpdateIdAsync(703, CancellationToken.None);
        roundTrip!.IdempotencyStatus.Should().Be(IdempotencyStatus.Processing,
            "the live claim must survive the reclaim pass");
        roundTrip.ProcessingStartedAt.Should().NotBeNull(
            "TryMarkProcessing must have stamped the lease timestamp");
    }

    // ============================================================
    // Helpers for the iter-3+ feedback tests.
    // ============================================================

    private InboundRecoverySweep NewSweepWithLogger(CapturingLoggerProvider capture, int maxRetries = 3)
    {
        var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory(new[] { capture });
        return new InboundRecoverySweep(
            _services.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<InboundRecoverySweep>(),
            TimeSpan.FromMinutes(1),
            maxRetries,
            TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// Test-only <see cref="ILoggerProvider"/> that captures every
    /// log entry into <see cref="Lines"/> so assertions can match
    /// on level + rendered message. Trivial enough that pulling in
    /// a logger-testing package is not justified.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<(LogLevel Level, string Message)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly System.Collections.Concurrent.ConcurrentBag<(LogLevel Level, string Message)> _sink;
            public CapturingLogger(System.Collections.Concurrent.ConcurrentBag<(LogLevel, string)> sink) { _sink = sink; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _sink.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
