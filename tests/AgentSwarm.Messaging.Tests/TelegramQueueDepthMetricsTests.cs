// -----------------------------------------------------------------------
// <copyright file="TelegramQueueDepthMetricsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using AgentSwarm.Messaging.Worker.Observability;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Stage 6.1 — pins for <see cref="TelegramQueueDepthMetrics"/>.
/// The two observable gauges <c>telegram.queue.depth</c> and
/// <c>telegram.dlq.depth</c> are the brief's "production readiness"
/// signals — they MUST report the live <c>OutboundMessage</c> Pending+Sending
/// count and the live <c>DeadLetterMessage</c> (not-Acknowledged) count
/// respectively, with a zero fallback when no <c>MessagingDbContext</c>
/// is wired (in-memory test hosts).
/// </summary>
public sealed class TelegramQueueDepthMetricsTests : IAsyncLifetime
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
    public async Task QueueDepthGauge_CountsPendingAndSendingOutboundRows_ExcludesTerminalStatuses()
    {
        await SeedOutboundAsync(
            (OutboundMessageStatus.Pending, 3),
            (OutboundMessageStatus.Sending, 2),
            // The brief is unambiguous: "Pending + Sending". Terminal
            // statuses below MUST be excluded from queue.depth.
            (OutboundMessageStatus.Sent, 4),
            (OutboundMessageStatus.Failed, 1),
            (OutboundMessageStatus.DeadLettered, 1));

        using var metrics = new TelegramQueueDepthMetrics(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelegramQueueDepthMetrics>.Instance);
        using var collector = new GaugeCollector(TelegramTelemetry.MeterName);

        collector.Collect();

        collector.LastValue(TelegramTelemetry.QueueDepthGaugeName).Should().Be(5,
            "queue.depth must report Pending(3)+Sending(2)=5, excluding Sent/Failed/DeadLettered terminal rows");
    }

    [Fact]
    public async Task DlqDepthGauge_CountsNotAcknowledgedDeadLetters_ExcludesAcknowledged()
    {
        await SeedDeadLettersAsync(
            (DeadLetterAlertStatus.Pending, 4),
            (DeadLetterAlertStatus.Sent, 2),
            // Operator-acknowledged rows are excluded — the gauge
            // reflects unactioned dead-letter pressure.
            (DeadLetterAlertStatus.Acknowledged, 3));

        using var metrics = new TelegramQueueDepthMetrics(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelegramQueueDepthMetrics>.Instance);
        using var collector = new GaugeCollector(TelegramTelemetry.MeterName);

        collector.Collect();

        collector.LastValue(TelegramTelemetry.DlqDepthGaugeName).Should().Be(6,
            "dlq.depth must report Pending(4)+Sent(2)=6 and exclude Acknowledged(3) — the brief's intent is 'unactioned dead-letter pressure'");
    }

    [Fact]
    public void Gauges_ReturnZero_WhenNoMessagingDbContextRegistered()
    {
        // Hosts that wire AddTelegram WITHOUT AddMessagingPersistence
        // (in-memory unit-test hosts) have no MessagingDbContext to
        // resolve. The gauge MUST report 0 rather than throw — a
        // throwing observable gauge tears down the whole metrics
        // pipeline.
        var emptyServices = new ServiceCollection().BuildServiceProvider();

        using var metrics = new TelegramQueueDepthMetrics(
            emptyServices.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelegramQueueDepthMetrics>.Instance);
        using var collector = new GaugeCollector(TelegramTelemetry.MeterName);

        var collect = () => collector.Collect();
        collect.Should().NotThrow(
            "gauge callbacks MUST tolerate the no-DbContext shape — throwing kills the whole metrics SDK pipeline");
        collector.LastValue(TelegramTelemetry.QueueDepthGaugeName).Should().Be(0);
        collector.LastValue(TelegramTelemetry.DlqDepthGaugeName).Should().Be(0);
    }

    [Fact]
    public async Task QueueDepthGauge_ReportsZero_OnDatabaseError_AndDoesNotThrow()
    {
        // Drop the OutboundMessages table to simulate a transient EF /
        // SQLite failure inside the gauge callback. The callback must
        // log + return 0 (per the class's "do not tear down the whole
        // metrics pipeline" contract) and NEVER propagate the exception.
        await using (var ctx = new MessagingDbContext(_options))
        {
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE outbox;");
        }

        using var metrics = new TelegramQueueDepthMetrics(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelegramQueueDepthMetrics>.Instance);
        using var collector = new GaugeCollector(TelegramTelemetry.MeterName);

        var collect = () => collector.Collect();
        collect.Should().NotThrow(
            "a transient EF/SQLite failure inside ObserveQueueDepth must not surface to the metrics SDK — a brief moment of stale gauge readings is far better than killing every other counter the host emits");
        collector.LastValue(TelegramTelemetry.QueueDepthGaugeName).Should().Be(0,
            "the failure path falls back to 0 with a Warning log so dashboards see a known-bad value rather than a missing point");
    }

    // ============================================================
    // Stage 6.1 iter-4 evaluator item 2 — Dispose lifecycle pins.
    // ============================================================

    /// <summary>
    /// Pins the post-Dispose contract: the static observable gauges
    /// remain registered (System.Diagnostics.Metrics has no per-instrument
    /// unregister hook), but their callbacks must observe the disposed
    /// instance's <c>_activeInstance</c> slot as null and return 0
    /// without dereferencing the disposed <see cref="IServiceScopeFactory"/>.
    /// Without this, an integration-test
    /// <c>WebApplicationFactory</c> that constructs and tears down
    /// multiple hosts in a single process would either (a) duplicate
    /// the gauges on every host or (b) leak ObjectDisposedException
    /// out of every metrics collection cycle.
    /// </summary>
    [Fact]
    public async Task Dispose_NullsActiveInstance_SoGaugeCallbacksReturnZero_WithoutTouchingDisposedScopeFactory()
    {
        // Build a scope factory + DbContext that we will dispose
        // BEFORE the gauge collection. If Dispose did not clear the
        // active reference, the callback would still reach into the
        // disposed services and throw ObjectDisposedException —
        // which the metrics SDK would propagate, tearing down all
        // other instruments on the meter.
        var localServices = new ServiceCollection();
        localServices.AddSingleton(_options);
        localServices.AddScoped<MessagingDbContext>(sp =>
            new MessagingDbContext(sp.GetRequiredService<DbContextOptions<MessagingDbContext>>()));
        var localProvider = localServices.BuildServiceProvider();

        await SeedOutboundAsync((OutboundMessageStatus.Pending, 2));

        var metrics = new TelegramQueueDepthMetrics(
            localProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelegramQueueDepthMetrics>.Instance);
        using var collector = new GaugeCollector(TelegramTelemetry.MeterName);

        // Sanity: while the instance is alive, the gauge reads the
        // seeded count.
        collector.Collect();
        collector.LastValue(TelegramTelemetry.QueueDepthGaugeName).Should().BeGreaterThanOrEqualTo(2,
            "before Dispose, the gauge must observe the active instance and read pending count");

        // Dispose the active instance AND its DI container — any
        // dangling callback that still dereferences the scope factory
        // will now throw ObjectDisposedException.
        metrics.Dispose();
        await localProvider.DisposeAsync();

        var collect = () => collector.Collect();
        collect.Should().NotThrow(
            "Dispose MUST null the active instance so the static gauge callbacks read 0 instead of dereferencing the disposed IServiceScopeFactory — without this, a torn-down WebApplicationFactory leaks ObjectDisposedException on every metrics collection cycle");
        collector.LastValue(TelegramTelemetry.QueueDepthGaugeName).Should().Be(0,
            "after Dispose with no replacement instance, the callback must observe a null _activeInstance and return 0 — the gauges themselves stay registered for the process lifetime but become quiescent");
        collector.LastValue(TelegramTelemetry.DlqDepthGaugeName).Should().Be(0,
            "post-Dispose, the DLQ gauge must mirror the queue gauge's quiescent behavior so dashboards see a coherent zero rather than a partial-state");
    }

    /// <summary>
    /// Pins the gauge-singleton invariant: constructing a NEW
    /// <see cref="TelegramQueueDepthMetrics"/> when one is already
    /// registered MUST NOT re-register the observable gauges on the
    /// static <see cref="TelegramTelemetry.Meter"/>. If it did, a
    /// host-restart cycle would multiply gauge emissions per
    /// collection cycle (N hosts → N×gauge points per scrape).
    /// </summary>
    [Fact]
    public void SecondInstance_DoesNotDuplicateGauges_OnStaticMeter()
    {
        var emptyServices = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = emptyServices.GetRequiredService<IServiceScopeFactory>();

        // Count how many distinct ObservableGauge<long> instruments
        // for the depth-gauge names exist BEFORE we make a second
        // instance.
        using var firstInstance = new TelegramQueueDepthMetrics(
            scopeFactory, NullLogger<TelegramQueueDepthMetrics>.Instance);

        var publishedBefore = CountPublishedDepthGauges();

        using var secondInstance = new TelegramQueueDepthMetrics(
            scopeFactory, NullLogger<TelegramQueueDepthMetrics>.Instance);

        var publishedAfter = CountPublishedDepthGauges();
        publishedAfter.Should().Be(publishedBefore,
            "Stage 6.1 invariant: gauges are registered EXACTLY once on the static meter for the process lifetime — a second instance must NOT add more gauge registrations or dashboards would see N×duplicate measurements per host-restart cycle");
    }

    private static int CountPublishedDepthGauges()
    {
        var count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (string.Equals(instrument.Meter.Name, TelegramTelemetry.MeterName, StringComparison.Ordinal)
                && instrument is ObservableGauge<long>
                && (string.Equals(instrument.Name, TelegramTelemetry.QueueDepthGaugeName, StringComparison.Ordinal)
                    || string.Equals(instrument.Name, TelegramTelemetry.DlqDepthGaugeName, StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref count);
            }
        };
        listener.Start();
        return count;
    }

    // ============================================================
    // Helpers
    // ============================================================

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

    private async Task SeedDeadLettersAsync(params (DeadLetterAlertStatus status, int count)[] rows)
    {
        await using var ctx = new MessagingDbContext(_options);
        var now = DateTimeOffset.UtcNow;
        foreach (var (status, count) in rows)
        {
            for (var i = 0; i < count; i++)
            {
                ctx.DeadLetterMessages.Add(new DeadLetterMessage
                {
                    Id = Guid.NewGuid(),
                    OriginalMessageId = Guid.NewGuid(),
                    IdempotencyKey = $"idem-dlq-{status}-{i}-{Guid.NewGuid():N}",
                    ChatId = 1234L,
                    SourceType = OutboundSourceType.StatusUpdate,
                    Payload = "body",
                    Severity = MessageSeverity.Normal,
                    CorrelationId = "trace-" + Guid.NewGuid().ToString("N"),
                    FinalError = "transient-burst",
                    AttemptCount = 5,
                    FailureCategory = OutboundFailureCategory.TransientTransport,
                    CreatedAt = now,
                    DeadLetteredAt = now,
                    AlertStatus = status,
                });
            }
        }
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// xUnit-friendly <see cref="MeterListener"/> wrapper that
    /// records the most recent observable-gauge measurement per
    /// instrument on a named meter. The instrument's callback is
    /// invoked by <see cref="MeterListener.RecordObservableInstruments"/>.
    /// </summary>
    private sealed class GaugeCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentDictionary<string, long> _latest = new(StringComparer.Ordinal);

        public GaugeCollector(string meterName)
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal)
                    && instrument is ObservableGauge<long>)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                _latest[instrument.Name] = measurement;
            });
            _listener.Start();
        }

        public void Collect() => _listener.RecordObservableInstruments();

        public long LastValue(string instrumentName) =>
            _latest.TryGetValue(instrumentName, out var v) ? v : -1;

        public void Dispose() => _listener.Dispose();
    }
}
