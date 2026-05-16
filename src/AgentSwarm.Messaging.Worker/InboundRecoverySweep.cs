using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Webhook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// Periodic background sweep that replays
/// <see cref="InboundUpdate"/> rows the dispatcher could not finish
/// (process crash mid-pipeline, transient pipeline exception, host
/// shutdown before drain). Reads
/// <see cref="IInboundUpdateStore.GetRecoverableAsync"/> at a
/// configurable interval (default 60s) and drives each row back
/// through <see cref="InboundUpdateProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded blast radius.</b> The sweep processes rows
/// sequentially. Concurrent replay of the same row is prevented by the
/// dispatcher's "Completed → skip" guard (which also applies here) and
/// by the in-memory dedup reservation released by the pipeline on
/// failure. Stage 4.3 will tighten this with a persistent dedup
/// table; the current shape is correct for in-memory state because a
/// restart wipes the reservation.
/// </para>
/// <para>
/// <b>MaxRetries / DeadLettering.</b> The sweep retrieves rows where
/// <see cref="InboundUpdate.AttemptCount"/> &lt; <c>MaxRetries</c>; rows
/// at or above the cap are surfaced via
/// <see cref="IInboundUpdateStore.GetExhaustedRetryCountAsync"/>
/// (total count, used for the metric/alert) and
/// <see cref="IInboundUpdateStore.GetExhaustedAsync"/> (capped row
/// sample, used for per-row Error logging). Stage 4.4 / 4.5 will add
/// a true dead-letter transition; Stage 2.4's contract is "do not
/// replay, expose count for alerting AND per-row diagnostics for
/// triage".
/// </para>
/// <para>
/// <b>Exhausted-retry alerting.</b> Every sweep tick calls
/// <see cref="IInboundUpdateStore.GetExhaustedRetryCountAsync"/> AFTER
/// the recoverable query (regardless of whether there was any
/// recoverable work) and emits the
/// <c>inbound_update_exhausted_retries</c> signal at
/// <see cref="LogLevel.Warning"/> when the count is non-zero — the
/// count is the TRUE total of exhausted rows, not the per-tick
/// sample size. The metric is the alert-on-the-secondary-channel
/// contract required by implementation-plan.md §201 and the story
/// brief's reliability row. The sweep ALSO calls
/// <see cref="IInboundUpdateStore.GetExhaustedAsync"/> with the
/// <see cref="ExhaustedRowsPerTickLimit"/> cap to produce one
/// <see cref="LogLevel.Error"/> log line per exhausted row containing
/// the <see cref="InboundUpdate.UpdateId"/> and
/// <see cref="InboundUpdate.ErrorDetail"/> per implementation-plan.md
/// §188. We log the total on every tick (not only on transitions)
/// because dropped-log scenarios should not silently mask a non-zero
/// count; when an <see cref="IAlertService"/> is registered in DI the
/// sweep ALSO calls it for explicit out-of-band notification with the
/// same per-row detail. The alert call is best-effort and a failed
/// alert does not fail the sweep.
/// </para>
/// <para>
/// <b>Why <see cref="PeriodicTimer"/> rather than
/// <c>Task.Delay</c>.</b> <see cref="PeriodicTimer"/> drifts less under
/// load (each tick is scheduled relative to the previous tick, not
/// relative to "now after the last sweep finished") and cleanly stops
/// on cancellation without races on the disposed timer handle.
/// </para>
/// <para>
/// <b>Correlation id propagation.</b> The sweep uses the
/// <see cref="InboundUpdate.CorrelationId"/> persisted by the original
/// webhook delivery as the trace identifier passed to the pipeline —
/// so a replayed row's outbound reply still carries the same trace
/// identifier as the original inbound. Only when the persisted row's
/// <c>CorrelationId</c> is <c>null</c>/blank (legacy rows persisted
/// before the column existed, or hand-seeded test data) does the sweep
/// fall back to a synthetic <c>sweep-&lt;id&gt;</c> identifier.
/// </para>
/// </remarks>
internal sealed class InboundRecoverySweep : BackgroundService
{
    public const string SweepIntervalKey = "InboundRecovery:SweepIntervalSeconds";
    public const string MaxRetriesKey = "InboundRecovery:MaxRetries";
    public const string StaleProcessingThresholdKey = "InboundRecovery:StaleProcessingThresholdSeconds";
    public const int DefaultSweepIntervalSeconds = 60;
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// Default lease/staleness window for the
    /// <see cref="IInboundUpdateStore.ReclaimStaleProcessingAsync"/>
    /// reclaim path (iter-5 evaluator item 3). 30 minutes is roughly
    /// three orders of magnitude above the story brief's 2-second P95
    /// send latency — a legitimately healthy handler will never
    /// approach this threshold, so the reclaim only fires on truly
    /// orphaned rows (crash mid-flight, swallowed-cancel-release).
    /// Operators tighten this in config when their inbound traffic
    /// pattern needs faster recovery, or loosen it when handlers
    /// occasionally take longer (e.g. a heavy batch operation).
    /// </summary>
    public const int DefaultStaleProcessingThresholdSeconds = 1800;

    /// <summary>
    /// Log scope / metric name surfaced when the recovery sweep observes
    /// rows that have exhausted their retry budget. Tests and operators
    /// pivot on this constant rather than the raw string so a rename is
    /// caught by the compiler.
    /// </summary>
    public const string ExhaustedRetriesMetric = "inbound_update_exhausted_retries";

    /// <summary>
    /// Hard cap on per-tick per-row Error log lines for exhausted-retry
    /// alerting. Large enough to surface a real operational incident
    /// (≥3 typical, &lt;~50 is the alert window), small enough to bound
    /// the log-line burst from a single sweep tick when the
    /// <c>inbound_updates</c> Failed-with-exhausted-attempts row count
    /// balloons (e.g. a downstream outage caused thousands of failures
    /// before the operator paused inflow). Triage past this cap should
    /// happen by querying the <c>inbound_updates</c> table directly,
    /// not by reading more log lines.
    /// </summary>
    public const int ExhaustedRowsPerTickLimit = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _sweepInterval;
    private readonly int _maxRetries;
    private readonly TimeSpan _staleProcessingThreshold;
    private readonly ILogger<InboundRecoverySweep> _logger;

    public InboundRecoverySweep(
        IServiceScopeFactory scopeFactory,
        ILogger<InboundRecoverySweep> logger,
        TimeSpan sweepInterval,
        int maxRetries)
        : this(scopeFactory, logger, sweepInterval, maxRetries,
            TimeSpan.FromSeconds(DefaultStaleProcessingThresholdSeconds))
    {
    }

    public InboundRecoverySweep(
        IServiceScopeFactory scopeFactory,
        ILogger<InboundRecoverySweep> logger,
        TimeSpan sweepInterval,
        int maxRetries,
        TimeSpan staleProcessingThreshold)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (sweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepInterval), sweepInterval, "must be positive.");
        }
        if (maxRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be positive.");
        }
        if (staleProcessingThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleProcessingThreshold), staleProcessingThreshold, "must be positive.");
        }
        _sweepInterval = sweepInterval;
        _maxRetries = maxRetries;
        _staleProcessingThreshold = staleProcessingThreshold;
    }

    /// <summary>
    /// Runs one sweep pass against the supplied service scope. Exposed
    /// internally so tests can drive a single iteration without waiting
    /// for the timer. Returns the number of rows the sweep actually
    /// drove through the pipeline (i.e. did not lose the
    /// <see cref="IInboundUpdateStore.TryMarkProcessingAsync"/> claim
    /// to a parallel worker and did not skip due to mid-pass status
    /// transitions).
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
        var processor = scope.ServiceProvider.GetRequiredService<InboundUpdateProcessor>();

        // Iter-5 evaluator item 3 — reclaim orphaned Processing rows
        // BEFORE GetRecoverableAsync. Without this step a row that
        // became orphaned AFTER startup (crash mid-pipeline, swallowed
        // ReleaseProcessingAsync exception) would surface in
        // GetRecoverableAsync but the processor's TryMarkProcessing CAS
        // would reject it (Processing is not eligible), so the pipeline
        // never re-runs — the row would strand until the next host
        // restart's one-shot ResetInterruptedAsync.
        //
        // The reclaim's WHERE clause is `ProcessingStartedAt IS NULL OR
        // ProcessingStartedAt < (UtcNow - staleness)`. Live (recently-
        // claimed) Processing rows have ProcessingStartedAt = UtcNow
        // and DO NOT match, so a healthy in-flight handler cannot be
        // falsely reset. The default staleness (30 minutes) is
        // ~1000× the story's 2-second P95 SLA — a wide safety margin.
        try
        {
            await store.ReclaimStaleProcessingAsync(_staleProcessingThreshold, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reclaim is a safety-net path; a failure here must not
            // block the rest of the sweep — the recoverable query
            // and exhausted-retry alert path still run. Log Error so
            // operators see the failure mode.
            _logger.LogError(
                ex,
                "InboundRecoverySweep ReclaimStaleProcessingAsync failed; proceeding with recoverable + exhausted alert paths.");
        }

        var recoverable = await store.GetRecoverableAsync(_maxRetries, ct).ConfigureAwait(false);

        // ALWAYS check exhausted-retry count, regardless of whether any
        // rows are recoverable — a zero-recoverable sweep can still
        // surface dead-lettered rows that the operator must see. This
        // is the canonical emission point for the
        // `inbound_update_exhausted_retries` signal (implementation-plan
        // §201).
        await EmitExhaustedRetryAlertAsync(scope.ServiceProvider, store, ct).ConfigureAwait(false);

        if (recoverable.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation(
            "InboundRecoverySweep replaying {Count} row(s).", recoverable.Count);

        var processed = 0;
        foreach (var row in recoverable)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Skip rows that flipped to Completed between the query and
            // the processing of THIS row in the same pass (the dispatcher
            // may have drained one in parallel).
            var current = await store.GetByUpdateIdAsync(row.UpdateId, ct).ConfigureAwait(false);
            if (current is null || current.IdempotencyStatus == IdempotencyStatus.Completed)
            {
                continue;
            }

            var correlationId = ResolveCorrelationId(current);
            try
            {
                // ProcessAsync returns false when TryMarkProcessingAsync
                // lost the CAS to a parallel worker (live dispatcher
                // already claimed this Processing row). Increment the
                // processed counter ONLY when the pipeline actually
                // ran — otherwise the sweep's return value /
                // observability would over-report work it did not
                // actually do (iter-4 evaluator item 4).
                var pipelineRan = await processor
                    .ProcessAsync(current, correlationId, ct)
                    .ConfigureAwait(false);
                if (pipelineRan)
                {
                    processed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "InboundRecoverySweep caught exception while replaying UpdateId={UpdateId} CorrelationId={CorrelationId}; will retry next pass.",
                    row.UpdateId,
                    correlationId);
            }
        }

        return processed;
    }

    /// <summary>
    /// Looks up the rows that have exhausted their retry budget and,
    /// when non-empty, emits the <see cref="ExhaustedRetriesMetric"/>
    /// alert at <see cref="LogLevel.Warning"/> (one summary line
    /// carrying the TRUE total exhausted count from
    /// <see cref="IInboundUpdateStore.GetExhaustedRetryCountAsync"/>)
    /// and then logs each row in a capped sample at
    /// <see cref="LogLevel.Error"/> with its
    /// <see cref="InboundUpdate.UpdateId"/>,
    /// <see cref="InboundUpdate.AttemptCount"/>, and
    /// <see cref="InboundUpdate.ErrorDetail"/> per implementation-plan.md
    /// §188 / §201. Also invokes any registered
    /// <see cref="IAlertService"/> with the same per-row detail.
    /// Failures inside the alert path are swallowed (logged at
    /// <see cref="LogLevel.Error"/>) so the sweep continues to run even
    /// if the alert sink is down — losing the alert is preferable to
    /// blocking the recovery sweep.
    /// </summary>
    /// <remarks>
    /// The metric/alert COUNT is sourced from
    /// <see cref="IInboundUpdateStore.GetExhaustedRetryCountAsync"/>
    /// (true total, not a sample). The per-row Error log lines are
    /// sourced from <see cref="IInboundUpdateStore.GetExhaustedAsync"/>
    /// with the <see cref="ExhaustedRowsPerTickLimit"/> cap applied so
    /// a flood of failures cannot produce an unbounded log burst from
    /// a single sweep tick. When the sample is smaller than the total,
    /// the warning line says so explicitly so the operator knows there
    /// are more rows to triage by direct database query.
    /// </remarks>
    private async Task EmitExhaustedRetryAlertAsync(
        IServiceProvider scopedServices,
        IInboundUpdateStore store,
        CancellationToken ct)
    {
        // Total count first — this is the metric/alert quantity. It
        // must NOT be derived from the per-row sample (which is capped
        // at ExhaustedRowsPerTickLimit and would underreport real
        // incidents) — iter-4 evaluator item 3.
        int totalExhausted;
        try
        {
            totalExhausted = await store
                .GetExhaustedRetryCountAsync(_maxRetries, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "InboundRecoverySweep failed to query exhausted-retry total; alert suppressed for this tick.");
            return;
        }

        if (totalExhausted <= 0)
        {
            // Emit at Information to provide a heartbeat that the sweep
            // is actually checking — operators tail this log line as a
            // liveness signal for the recovery sweep.
            _logger.LogInformation(
                "Metric={Metric} ExhaustedRetryCount=0 MaxRetries={MaxRetries}",
                ExhaustedRetriesMetric,
                _maxRetries);
            return;
        }

        // Per-row sample — capped to bound the log-line burst from a
        // single sweep tick. The cap is decoupled from the total
        // count above: log lines are bounded but the metric is honest.
        IReadOnlyList<InboundUpdate> exhaustedRows;
        try
        {
            exhaustedRows = await store
                .GetExhaustedAsync(_maxRetries, ExhaustedRowsPerTickLimit, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "InboundRecoverySweep failed to query per-row exhausted sample (total was {TotalExhausted}); per-row diagnostics suppressed for this tick.",
                totalExhausted);
            // Still emit the summary metric: count is independently
            // observed and must not be lost just because the sample
            // query failed.
            _logger.LogWarning(
                "Metric={Metric} ExhaustedRetryCount={Count} MaxRetries={MaxRetries} — these rows have exhausted retries and require operator intervention. (Per-row sample query failed; operator should query the inbound_updates table directly.)",
                ExhaustedRetriesMetric,
                totalExhausted,
                _maxRetries);
            return;
        }

        var sampledCount = exhaustedRows.Count;
        var truncated = sampledCount < totalExhausted;
        if (truncated)
        {
            _logger.LogWarning(
                "Metric={Metric} ExhaustedRetryCount={Count} MaxRetries={MaxRetries} SampledRowCount={Sampled} (capped at {Cap}; remaining {Overflow} row(s) are not in this tick's per-row log — query inbound_updates directly to triage).",
                ExhaustedRetriesMetric,
                totalExhausted,
                _maxRetries,
                sampledCount,
                ExhaustedRowsPerTickLimit,
                totalExhausted - sampledCount);
        }
        else
        {
            _logger.LogWarning(
                "Metric={Metric} ExhaustedRetryCount={Count} MaxRetries={MaxRetries} — these rows have exhausted retries and require operator intervention.",
                ExhaustedRetriesMetric,
                totalExhausted,
                _maxRetries);
        }

        // Per-row Error logging — implementation-plan.md §188/§201
        // explicitly requires the UpdateId + ErrorDetail so an operator
        // tailing logs can identify the specific stranded updates
        // without joining against the database.
        foreach (var row in exhaustedRows)
        {
            _logger.LogError(
                "Metric={Metric} UpdateId={UpdateId} AttemptCount={AttemptCount} CorrelationId={CorrelationId} ErrorDetail={ErrorDetail} — inbound update exhausted retries (manual intervention required).",
                ExhaustedRetriesMetric,
                row.UpdateId,
                row.AttemptCount,
                row.CorrelationId ?? "(unset)",
                row.ErrorDetail ?? "(none)");
        }

        var alertService = scopedServices.GetService<IAlertService>();
        if (alertService is null)
        {
            return;
        }

        try
        {
            var subject = "Inbound update recovery: " + totalExhausted + " row(s) exhausted retries";
            var detailBuilder = new System.Text.StringBuilder();
            detailBuilder.Append("Metric=").Append(ExhaustedRetriesMetric)
                .Append(" Count=").Append(totalExhausted.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" MaxRetries=").Append(_maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(". The following rows remain in IdempotencyStatus=Failed and are excluded from future sweeps")
                .Append(truncated
                    ? " (showing first " + sampledCount + " of " + totalExhausted + " — query inbound_updates directly for the rest):"
                    : ":")
                .AppendLine();
            foreach (var row in exhaustedRows)
            {
                detailBuilder
                    .Append("  - UpdateId=").Append(row.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" AttemptCount=").Append(row.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" CorrelationId=").Append(row.CorrelationId ?? "(unset)")
                    .Append(" ErrorDetail=").Append(row.ErrorDetail ?? "(none)")
                    .AppendLine();
            }
            await alertService.SendAlertAsync(subject, detailBuilder.ToString(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "InboundRecoverySweep IAlertService.SendAlertAsync threw; the per-row exhausted-retry log lines are still authoritative.");
        }
    }

    /// <summary>
    /// Returns the persisted <see cref="InboundUpdate.CorrelationId"/>
    /// when present, otherwise a synthetic <c>sweep-&lt;id&gt;</c>
    /// trace id keyed on the Telegram update id so legacy rows
    /// (persisted before the column existed) still flow through
    /// <see cref="InboundUpdateProcessor.ProcessAsync"/> (which rejects
    /// blank correlation ids).
    /// </summary>
    internal static string ResolveCorrelationId(InboundUpdate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !string.IsNullOrWhiteSpace(row.CorrelationId)
            ? row.CorrelationId!
            : "sweep-" + row.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "InboundRecoverySweep started. Interval={Interval}, MaxRetries={MaxRetries}",
            _sweepInterval, _maxRetries);

        using var timer = new PeriodicTimer(_sweepInterval);
        try
        {
            // Run an initial sweep at startup so crash-recovery does not
            // wait a full interval for orphaned rows.
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "InboundRecoverySweep tick failed; will retry next interval.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InboundRecoverySweep stopped.");
        }
    }
}

/// <summary>
/// Bridges <see cref="IConfiguration"/> to
/// <see cref="InboundRecoverySweep"/>'s constructor arguments. Kept as
/// a tiny strongly-typed options class so the Worker's Program can
/// register and the test harness can override without touching the
/// hosted service directly.
/// </summary>
internal sealed class InboundRecoveryOptions
{
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(InboundRecoverySweep.DefaultSweepIntervalSeconds);

    public int MaxRetries { get; init; } = InboundRecoverySweep.DefaultMaxRetries;
}
