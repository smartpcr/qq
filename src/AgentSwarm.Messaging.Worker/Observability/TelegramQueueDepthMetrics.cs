// -----------------------------------------------------------------------
// <copyright file="TelegramQueueDepthMetrics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker.Observability;

using System;
using System.Diagnostics.Metrics;
using System.Threading;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 6.1 — registers the <c>telegram.queue.depth</c> and
/// <c>telegram.dlq.depth</c> observable gauges on the shared
/// <see cref="TelegramTelemetry.Meter"/>. Each gauge is a callback
/// the metrics SDK invokes on every collection cycle; the callback
/// opens a fresh DI scope, materialises <see cref="MessagingDbContext"/>,
/// and runs a single <c>COUNT(*)</c> against
/// <c>outbound_messages</c> (Pending + Sending) or
/// <c>dead_letter_messages</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate type instead of inline lambdas in Program.cs.</b>
/// Observable gauges hold a reference to the callback for the lifetime
/// of the <see cref="Meter"/>, which lives until process shutdown.
/// Capturing <c>app.Services</c> inside a top-level-statement lambda
/// keeps the host's IServiceProvider alive for the whole process —
/// which is fine for production but breaks integration-test
/// isolation (xUnit constructs and tears down the WebApplicationFactory
/// many times per process). Encapsulating the callback inside a
/// singleton means the integration-test factory can dispose the
/// instrument when the host shuts down.
/// </para>
/// <para>
/// <b>Failure mode.</b> A transient EF Core / SQLite error inside the
/// callback returns <c>0</c> with a Warning log; the metrics SDK
/// retries on the next collection cycle. Throwing would tear down
/// the entire metrics pipeline, which is the wrong trade-off — a
/// momentarily-stale depth gauge is far less disruptive than losing
/// every other counter and histogram emitted by the process.
/// </para>
/// </remarks>
public sealed class TelegramQueueDepthMetrics : IDisposable
{
    // Stage 6.1 iter-4 evaluator item 2 — observable gauges are
    // registered on the STATIC TelegramTelemetry.Meter. The static
    // meter lives for the process lifetime, but the
    // TelegramQueueDepthMetrics singleton's _scopeFactory may
    // belong to a DI container that is torn down and rebuilt
    // (xUnit WebApplicationFactory does this many times per test
    // process). Without coordination:
    //   (a) Each new singleton would call CreateObservableGauge
    //       again, adding ANOTHER pair of callbacks for the same
    //       gauge name on the static meter — the SDK would then
    //       publish the gauge N times per collection cycle.
    //   (b) Old singletons whose containers have been disposed
    //       would still have their callbacks invoked, which
    //       touches disposed IServiceProvider state and either
    //       throws ObjectDisposedException or silently retains
    //       references the GC can't collect.
    //
    // The fix:
    //   - _gaugesRegistered guards CreateObservableGauge to fire
    //     EXACTLY once per process; subsequent instances rely on
    //     the already-registered callbacks.
    //   - _activeInstance is the shared mailbox the static gauge
    //     callbacks read. Each new instance becomes active in its
    //     ctor; Dispose null's the active reference iff this
    //     instance is still active. The callbacks return 0 when
    //     _activeInstance is null (post-Dispose / pre-activation),
    //     never touching a disposed scope factory.
    private static int _gaugesRegistered;
    private static volatile TelegramQueueDepthMetrics? _activeInstance;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramQueueDepthMetrics> _logger;
    private int _disposed;

    public TelegramQueueDepthMetrics(
        IServiceScopeFactory scopeFactory,
        ILogger<TelegramQueueDepthMetrics> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // This instance is now the authoritative source for the
        // static gauges' callbacks. Any previously-active instance
        // (e.g. an earlier WebApplicationFactory in the same test
        // process) is replaced atomically; its Dispose CAS will
        // see a different _activeInstance and become a no-op for
        // the static reference, which is exactly what we want
        // (the old instance must not null out the live one).
        _activeInstance = this;

        // CreateObservableGauge is process-singleton: gauges are
        // registered ONCE on the static meter, and their callbacks
        // dispatch through _activeInstance so per-instance state
        // (scope factory, logger) can rotate freely.
        if (Interlocked.CompareExchange(ref _gaugesRegistered, 1, 0) == 0)
        {
            TelegramTelemetry.Meter.CreateObservableGauge(
                TelegramTelemetry.QueueDepthGaugeName,
                () => _activeInstance?.ObserveQueueDepth() ?? 0,
                unit: "messages",
                description: "Current count of OutboundMessage rows with Status in (Pending, Sending) — live outbound queue depth.");

            TelegramTelemetry.Meter.CreateObservableGauge(
                TelegramTelemetry.DlqDepthGaugeName,
                () => _activeInstance?.ObserveDlqDepth() ?? 0,
                unit: "messages",
                description: "Current count of DeadLetterMessage rows whose AlertStatus is not Acknowledged — live dead-letter depth.");
        }
    }

    /// <summary>
    /// Releases this instance as the active source for the static
    /// gauge callbacks. The gauges themselves remain registered on
    /// the static meter for the process lifetime (the System.Diagnostics.Metrics
    /// API has no per-instrument unregister hook short of disposing
    /// the owning meter, which would tear down every other
    /// instrument on it). However, after Dispose the callbacks
    /// observe a null <c>_activeInstance</c> and return 0 without
    /// dereferencing this instance's <see cref="IServiceScopeFactory"/>
    /// — preventing disposed-container retention and the per-cycle
    /// ObjectDisposedException that would otherwise propagate out
    /// of the metrics SDK.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Only clear the active reference if WE are still the
        // active instance. A later-constructed instance may have
        // already taken over the static slot; the older instance's
        // Dispose must not strand it by null'ing a live reference.
        Interlocked.CompareExchange(ref _activeInstance, null, this);
    }

    private long ObserveQueueDepth()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<MessagingDbContext>();
            if (db is null)
            {
                // In-memory test hosts that wire AddTelegram but not
                // AddMessagingPersistence have no EF context — the
                // gauge is a no-op (zero) for them. This is expected
                // and intentional; the queue does not exist.
                return 0;
            }

            return db.OutboundMessages
                .AsNoTracking()
                .LongCount(x =>
                    x.Status == OutboundMessageStatus.Pending
                    || x.Status == OutboundMessageStatus.Sending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "telegram.queue.depth gauge collection failed; reporting 0 for this cycle.");
            return 0;
        }
    }

    private long ObserveDlqDepth()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<MessagingDbContext>();
            if (db is null)
            {
                return 0;
            }

            return db.DeadLetterMessages
                .AsNoTracking()
                .LongCount(x => x.AlertStatus != DeadLetterAlertStatus.Acknowledged);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "telegram.dlq.depth gauge collection failed; reporting 0 for this cycle.");
            return 0;
        }
    }
}
