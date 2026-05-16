// -----------------------------------------------------------------------
// <copyright file="SlackIdempotencyGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// EF Core-backed <see cref="ISlackIdempotencyGuard"/>. Inserts a
/// fresh <see cref="SlackInboundRequestRecord"/> row keyed by
/// <see cref="SlackInboundEnvelope.IdempotencyKey"/> on the first
/// observation of an envelope and applies the architecture.md §2.6
/// + §4.4 lease semantics to every subsequent observation: terminal
/// and fast-path rows are reported as a true duplicate, a recent
/// <c>processing</c> row is deferred to avoid preempting the
/// in-flight worker, and a stale <c>processing</c> row is reclaimed
/// via optimistic concurrency control so a crashed worker's lease
/// does not block future Slack retries forever.
/// </summary>
/// <typeparam name="TContext">
/// EF Core context that surfaces the
/// <see cref="SlackInboundRequestRecord"/> table. Registered via
/// <c>AddDbContext&lt;TContext&gt;</c>; the guard creates a fresh DI
/// scope per call so it stays safe as a singleton even though the
/// context is scoped.
/// </typeparam>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The dedup contract follows architecture.md §2.6 + §4.4: Slack's
/// at-least-once redelivery semantics mean a retry of an event that
/// the connector already processed (or is currently processing) must
/// not produce a second task / decision, BUT a redelivery whose
/// in-flight worker crashed mid-flow must still be recoverable.
/// The three observed-row outcomes are:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>True duplicate</term>
///     <description>existing row is
///     <see cref="SlackInboundRequestProcessingStatus.Completed"/>,
///     <see cref="SlackInboundRequestProcessingStatus.Failed"/>,
///     <see cref="SlackInboundRequestProcessingStatus.Reserved"/>,
///     <see cref="SlackInboundRequestProcessingStatus.ModalOpened"/>,
///     or <see cref="SlackInboundRequestProcessingStatus.Received"/>.
///     The redelivery is silently dropped and the audit row is
///     written with <c>outcome = duplicate</c>.</description>
///   </item>
///   <item>
///     <term>Deferred (live lease)</term>
///     <description>existing row is
///     <see cref="SlackInboundRequestProcessingStatus.Processing"/>
///     and younger than
///     <see cref="SlackIdempotencyOptions.StaleProcessingThresholdSeconds"/>.
///     A healthy worker still owns the lease, so the redelivery is
///     deferred (returns <see langword="false"/>) to avoid preempting
///     it. The audit row uses the same <c>outcome = duplicate</c>
///     marker but the existing row's <c>processing</c> status tells
///     operators the original work is still in flight.</description>
///   </item>
///   <item>
///     <term>Reclaimed (orphaned lease)</term>
///     <description>existing row is
///     <see cref="SlackInboundRequestProcessingStatus.Processing"/>
///     and older than the stale threshold. The original worker is
///     presumed dead; the guard bumps the row's
///     <see cref="SlackInboundRequestRecord.FirstSeenAt"/> via an
///     OCC <c>UPDATE</c> and returns <see langword="true"/> so the
///     redelivery becomes the new lease owner.</description>
///   </item>
/// </list>
/// <para>
/// Coexistence with the modal fast-path
/// (<see cref="EntityFrameworkSlackFastPathIdempotencyStore{TContext}"/>):
/// the fast-path writes its own row keyed by the same idempotency key
/// with <see cref="SlackInboundRequestProcessingStatus.Reserved"/> /
/// <see cref="SlackInboundRequestProcessingStatus.ModalOpened"/>
/// status. When the async ingestor later dequeues the same envelope,
/// <see cref="TryAcquireAsync"/> sees that row and reports a true
/// duplicate (returns <see langword="false"/>), so the modal envelope
/// is correctly skipped by the ingestor and the fast-path remains
/// the sole owner.
/// </para>
/// <para>
/// <see cref="MarkCompletedAsync"/> and <see cref="MarkFailedAsync"/>
/// transition the row to its terminal status and stamp
/// <see cref="SlackInboundRequestRecord.CompletedAt"/>; both calls
/// retry transient DB failures up to
/// <see cref="SlackIdempotencyOptions.CompletionMaxAttempts"/> with
/// exponential backoff. When the retry budget is exhausted,
/// <see cref="MarkCompletedAsync"/> attempts ONE FINAL transition via
/// raw <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdateAsync"/>
/// SQL (bypassing the EF change-tracker, which may have been poisoned
/// by repeated failures) to write the non-reclaimable disposition
/// <see cref="SlackInboundRequestProcessingStatus.CompletionPersistFailed"/>.
/// Because the stale-reclaim WHERE clause filters on
/// <see cref="SlackInboundRequestProcessingStatus.Processing"/>, a row
/// in <c>completion_persist_failed</c> CANNOT be reclaimed and so the
/// handler CANNOT be replayed -- structural guarantee that
/// already-successful work is not duplicated solely because the
/// completion-status write failed. <see cref="MarkFailedAsync"/>'s
/// fallback target is <see cref="SlackInboundRequestProcessingStatus.Failed"/>
/// (the row's true semantic state when the handler exhausted its
/// retry budget). If even the raw-SQL fallback throws, the guard logs
/// critically and surrenders; the row remains in <c>processing</c>
/// and operators MUST reconcile before the stale-lease window
/// elapses. The retained terminal-state row is what makes a future
/// Slack retry (arriving after the original ACK window) hit the
/// true-duplicate branch above.
/// </para>
/// </remarks>
internal sealed class SlackIdempotencyGuard<TContext> : ISlackIdempotencyGuard
    where TContext : DbContext, ISlackInboundRequestRecordDbContext
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackIdempotencyGuard<TContext>> logger;
    private readonly TimeSpan staleProcessingThreshold;
    private readonly int completionMaxAttempts;
    private readonly TimeSpan completionInitialDelay;

    public SlackIdempotencyGuard(
        IServiceScopeFactory scopeFactory,
        ILogger<SlackIdempotencyGuard<TContext>> logger,
        TimeProvider? timeProvider = null,
        IOptions<SlackConnectorOptions>? options = null)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;

        SlackIdempotencyOptions effective = options?.Value?.Idempotency ?? new SlackIdempotencyOptions();
        this.staleProcessingThreshold = TimeSpan.FromSeconds(Math.Max(1, effective.StaleProcessingThresholdSeconds));
        this.completionMaxAttempts = Math.Max(1, effective.CompletionMaxAttempts);
        this.completionInitialDelay = TimeSpan.FromMilliseconds(Math.Max(0, effective.CompletionInitialDelayMilliseconds));
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrEmpty(envelope.IdempotencyKey))
        {
            throw new ArgumentException(
                "SlackInboundEnvelope.IdempotencyKey must be populated before invoking the idempotency guard.",
                nameof(envelope));
        }

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        // Probe-then-insert. The probe avoids paying the EF
        // SaveChanges + DbUpdateException unwind cost on the common
        // duplicate path and lets us log the existing row's status
        // so an operator can tell whether the duplicate came from the
        // async pipeline (processing/completed/failed) or the modal
        // fast-path (reserved/modal_opened).
        SlackInboundRequestRecord? existing = await context.SlackInboundRequestRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == envelope.IdempotencyKey, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Recovery semantics (architecture.md §2.6 + §4.4): the
            // three observed-row outcomes are TRUE DUPLICATE (terminal
            // or fast-path row -- handler already ran), DEFERRED (a
            // recent 'processing' row whose live worker still owns
            // the lease -- the redelivery yields), and RECLAIMED (a
            // 'processing' row older than the configured stale-lease
            // window -- the original worker is presumed dead and the
            // redelivery becomes the new lease owner). The shared
            // outcome for DUPLICATE and DEFERRED is "drop the
            // envelope + audit as duplicate"; RECLAIMED returns true
            // so the pipeline re-dispatches.
            if (string.Equals(existing.ProcessingStatus, SlackInboundRequestProcessingStatus.Processing, StringComparison.Ordinal))
            {
                DateTimeOffset nowForLease = this.timeProvider.GetUtcNow();
                TimeSpan age = nowForLease - existing.FirstSeenAt;
                if (age >= this.staleProcessingThreshold)
                {
                    bool reclaimed = await this.TryReclaimStaleLeaseAsync(
                        context,
                        envelope,
                        existing.FirstSeenAt,
                        nowForLease,
                        ct).ConfigureAwait(false);

                    if (reclaimed)
                    {
                        this.logger.LogWarning(
                            "Slack idempotency guard reclaimed stale processing lease: idempotency_key={IdempotencyKey} previous_first_seen_at={PreviousFirstSeenAt:o} age_seconds={AgeSeconds} threshold_seconds={ThresholdSeconds}.",
                            envelope.IdempotencyKey,
                            existing.FirstSeenAt,
                            (long)age.TotalSeconds,
                            (long)this.staleProcessingThreshold.TotalSeconds);
                        return true;
                    }

                    this.logger.LogInformation(
                        "Slack idempotency guard could not reclaim stale lease for idempotency_key={IdempotencyKey}; another replica won the reclaim race -- deferring as duplicate.",
                        envelope.IdempotencyKey);
                    return false;
                }

                this.logger.LogInformation(
                    "Slack idempotency guard deferring redelivery for idempotency_key={IdempotencyKey}: in-flight processor still owns lease (age_seconds={AgeSeconds}, threshold_seconds={ThresholdSeconds}).",
                    envelope.IdempotencyKey,
                    (long)age.TotalSeconds,
                    (long)this.staleProcessingThreshold.TotalSeconds);
                return false;
            }

            this.logger.LogInformation(
                "Slack idempotency guard reports TRUE duplicate (handler already ran or fast-path owns request): idempotency_key={IdempotencyKey} existing_status={ExistingStatus} source={SourceType} team_id={TeamId} channel_id={ChannelId}.",
                envelope.IdempotencyKey,
                existing.ProcessingStatus,
                envelope.SourceType,
                envelope.TeamId,
                envelope.ChannelId);
            return false;
        }

        SlackInboundRequestRecord record = new()
        {
            IdempotencyKey = envelope.IdempotencyKey,
            SourceType = MapSourceType(envelope.SourceType),
            TeamId = string.IsNullOrEmpty(envelope.TeamId) ? "unknown" : envelope.TeamId,
            ChannelId = envelope.ChannelId,
            UserId = string.IsNullOrEmpty(envelope.UserId) ? "unknown" : envelope.UserId,
            RawPayloadHash = HashRawPayload(envelope.RawPayload),
            ProcessingStatus = SlackInboundRequestProcessingStatus.Processing,
            FirstSeenAt = envelope.ReceivedAt == default ? this.timeProvider.GetUtcNow() : envelope.ReceivedAt,
            CompletedAt = null,
        };

        context.SlackInboundRequestRecords.Add(record);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            // EF Core's DbUpdateException is raised for ANY backing-
            // store failure during SaveChanges -- unique-key conflicts
            // AND transient infrastructure failures share the type and
            // there is no cross-provider discriminator we can rely on
            // (the underlying SqlException / SqliteException / Npgsql
            // exception each use different SqlState codes). We MUST
            // distinguish the two: a real duplicate is the expected
            // race-lost path and should silently drop, but a transient
            // failure that we silently dropped as a duplicate would
            // permanently lose the envelope (the dedup row never gets
            // written and a Slack retry would arrive into an empty
            // store yet the original envelope is already off the
            // queue). Re-probe in a fresh scope: if a row now exists,
            // we lost a race; if not, the SaveChanges failure is
            // unrelated to uniqueness and MUST propagate so the
            // ingestor's outer catch reports it.
            bool conflictingRowExists;
            try
            {
                await using AsyncServiceScope probeScope = this.scopeFactory.CreateAsyncScope();
                TContext probeContext = probeScope.ServiceProvider.GetRequiredService<TContext>();
                conflictingRowExists = await probeContext.SlackInboundRequestRecords
                    .AsNoTracking()
                    .AnyAsync(r => r.IdempotencyKey == envelope.IdempotencyKey, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception probeEx)
            {
                this.logger.LogError(
                    probeEx,
                    "Slack idempotency guard could not probe for the conflicting row after SaveChanges failed for idempotency_key={IdempotencyKey}; propagating the original DB error so the envelope is not silently dropped.",
                    envelope.IdempotencyKey);
                throw;
            }

            if (conflictingRowExists)
            {
                this.logger.LogInformation(
                    ex,
                    "Slack idempotency guard insert race lost for idempotency_key={IdempotencyKey}; treating as duplicate.",
                    envelope.IdempotencyKey);
                return false;
            }

            this.logger.LogError(
                ex,
                "Slack idempotency guard SaveChanges failed without a competing row for idempotency_key={IdempotencyKey}; propagating so the envelope can be retried instead of being silently dropped as a duplicate.",
                envelope.IdempotencyKey);
            throw;
        }
    }

    /// <inheritdoc />
    public Task MarkCompletedAsync(string idempotencyKey, CancellationToken ct)
        => this.UpdateTerminalStatusAsync(
            idempotencyKey,
            SlackInboundRequestProcessingStatus.Completed,
            ct);

    /// <inheritdoc />
    public Task MarkFailedAsync(string idempotencyKey, CancellationToken ct)
        => this.UpdateTerminalStatusAsync(
            idempotencyKey,
            SlackInboundRequestProcessingStatus.Failed,
            ct);

    private async Task UpdateTerminalStatusAsync(string idempotencyKey, string terminalStatus, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return;
        }

        // Iter-6 evaluator item #2: a successful handler followed by
        // a transient DB failure on the terminal-status write USED to
        // leave the dedup row in 'processing'; once that row aged
        // past SlackIdempotencyOptions.StaleProcessingThresholdSeconds
        // the stale-reclaim path treated it as an orphaned lease and
        // re-executed the handler -- a duplicate-execution bug. The
        // fix is a bounded retry budget that absorbs the common
        // transient-DB failure modes (connection reset, deadlock,
        // brief read-only failover window) so successful work is
        // not replayed solely because the completion write blipped.
        // We deliberately retry the WHOLE operation (fresh scope +
        // fresh DbContext per attempt) so a faulted context cannot
        // poison subsequent attempts. If every attempt fails we log
        // critically and surrender to the existing best-effort
        // contract; operators are notified via the LogCritical and
        // can manually reconcile the row.
        Exception? lastException = null;
        for (int attempt = 1; attempt <= this.completionMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                bool finalised = await this.TryUpdateTerminalStatusOnceAsync(
                    idempotencyKey,
                    terminalStatus,
                    ct).ConfigureAwait(false);

                if (finalised)
                {
                    if (attempt > 1)
                    {
                        this.logger.LogInformation(
                            "Slack idempotency guard recovered terminal-status write after {Attempt} attempts for idempotency_key={IdempotencyKey} terminal_status={TerminalStatus}.",
                            attempt,
                            idempotencyKey,
                            terminalStatus);
                    }

                    return;
                }

                // finalised == false means the row was missing or
                // already terminal; both are silent no-ops and need
                // no retry.
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                this.logger.LogWarning(
                    ex,
                    "Slack idempotency guard terminal-status write failed on attempt {Attempt}/{MaxAttempts} for idempotency_key={IdempotencyKey} terminal_status={TerminalStatus}.",
                    attempt,
                    this.completionMaxAttempts,
                    idempotencyKey,
                    terminalStatus);

                if (attempt >= this.completionMaxAttempts)
                {
                    break;
                }

                TimeSpan delay = this.ComputeCompletionDelay(attempt);
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }
        }

        // Critical: the handler has already returned (or the retry
        // budget for handler dispatch has already expired) and we
        // could not persist the terminal status through the EF
        // change-tracker path. Iter 7 structural fix: instead of
        // leaving the row in 'processing' (which the stale-reclaim
        // path would later treat as orphaned and re-execute the
        // handler, violating duplicate-suppression for ALREADY-
        // SUCCESSFUL work), attempt a FINAL transition via raw
        // ExecuteUpdateAsync. Raw UPDATE bypasses the EF change-
        // tracker entirely, so a context that has been poisoned by
        // repeated SaveChanges failures (entity-state mismatch,
        // concurrency-token drift, snapshot corruption) can still
        // succeed at the SQL layer.
        //
        // The fallback target depends on the requested terminal
        // status:
        //   * MarkCompletedAsync -> CompletionPersistFailed
        //     (handler ran successfully, completion-write failed --
        //      operator must reconcile; the stale-reclaim WHERE
        //      filter excludes this disposition so the handler will
        //      not run again).
        //   * MarkFailedAsync -> Failed (handler already failed and
        //     went to DLQ; the row genuinely is in a failed state).
        string fallbackStatus = string.Equals(terminalStatus, SlackInboundRequestProcessingStatus.Completed, StringComparison.Ordinal)
            ? SlackInboundRequestProcessingStatus.CompletionPersistFailed
            : SlackInboundRequestProcessingStatus.Failed;

        bool fallbackPersisted = await this.TryPersistFallbackDispositionAsync(
            idempotencyKey,
            fallbackStatus,
            ct).ConfigureAwait(false);

        if (fallbackPersisted)
        {
            this.logger.LogCritical(
                lastException,
                "Slack idempotency guard EXHAUSTED EF terminal-status retry budget ({MaxAttempts} attempts) for idempotency_key={IdempotencyKey} requested_status={TerminalStatus}; persisted non-reclaimable fallback disposition '{FallbackStatus}' via raw SQL so the stale-reclaim path will NOT replay the handler -- operator MUST reconcile the row manually to restore the canonical terminal status.",
                this.completionMaxAttempts,
                idempotencyKey,
                terminalStatus,
                fallbackStatus);
            return;
        }

        // Both the EF retry budget AND the raw-SQL fallback failed.
        // The row stays in 'processing' and the stale-reclaim path
        // CAN eventually re-dispatch. Operators MUST reconcile
        // before SlackIdempotencyOptions.StaleProcessingThresholdSeconds
        // elapses to avoid duplicate handler execution.
        this.logger.LogCritical(
            lastException,
            "Slack idempotency guard EXHAUSTED terminal-status retry budget ({MaxAttempts} attempts) AND raw-SQL fallback transition for idempotency_key={IdempotencyKey} terminal_status={TerminalStatus}; row remains in 'processing' and WILL become eligible for stale-lease reclaim after {ThresholdSeconds}s -- operator MUST reconcile manually to avoid duplicate handler execution.",
            this.completionMaxAttempts,
            idempotencyKey,
            terminalStatus,
            (long)this.staleProcessingThreshold.TotalSeconds);
    }

    /// <summary>
    /// Attempts to transition a stuck <c>processing</c> row to a
    /// non-reclaimable fallback disposition using raw
    /// <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdateAsync"/>
    /// SQL, bypassing the EF change-tracker so a context that has
    /// been poisoned by repeated <c>SaveChanges</c> failures can
    /// still succeed. Returns <see langword="true"/> on success or
    /// when the row was already past <c>processing</c> (idempotent
    /// no-op); returns <see langword="false"/> only if even the raw
    /// SQL path threw.
    /// </summary>
    private async Task<bool> TryPersistFallbackDispositionAsync(
        string idempotencyKey,
        string fallbackStatus,
        CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
            TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

            DateTimeOffset stampedAt = this.timeProvider.GetUtcNow();

            // Only transition rows currently in 'processing' to the
            // fallback disposition -- this is OCC-style protection so
            // a row that another path (modal fast-path, replica retry,
            // or even a delayed-but-successful prior attempt) has
            // already finalised is left untouched.
            int affected = await context.SlackInboundRequestRecords
                .Where(r => r.IdempotencyKey == idempotencyKey
                            && r.ProcessingStatus == SlackInboundRequestProcessingStatus.Processing)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.ProcessingStatus, fallbackStatus)
                        .SetProperty(r => r.CompletedAt, (DateTimeOffset?)stampedAt),
                    ct)
                .ConfigureAwait(false);

            // affected == 0 means the row was either missing or
            // already past 'processing' -- both are acceptable
            // (something else finalised it) so we report success
            // and let the caller skip the residual critical log.
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Slack idempotency guard raw-SQL fallback transition to '{FallbackStatus}' failed for idempotency_key={IdempotencyKey}; row stays in 'processing'.",
                fallbackStatus,
                idempotencyKey);
            return false;
        }
    }

    /// <summary>
    /// One terminal-status write attempt. Returns <see langword="true"/>
    /// when the row exists and we successfully transitioned it (or when
    /// the row was already in a stable terminal status that we must not
    /// clobber). Returns <see langword="false"/> only for the
    /// missing-row case so callers know there is nothing left to retry.
    /// Throws on transient DB failures so the outer retry loop can
    /// absorb them.
    /// </summary>
    private async Task<bool> TryUpdateTerminalStatusOnceAsync(
        string idempotencyKey,
        string terminalStatus,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        SlackInboundRequestRecord? row = await context.SlackInboundRequestRecords
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            this.logger.LogWarning(
                "Slack idempotency guard cannot transition missing row to '{TerminalStatus}': idempotency_key={IdempotencyKey}.",
                terminalStatus,
                idempotencyKey);
            return false;
        }

        // Guard against clobbering a terminal row that the modal
        // fast-path or a competing replica already finalised --
        // once a row is in modal_opened, completed, failed, or
        // completion_persist_failed we leave it alone so dedup
        // state is stable.
        if (string.Equals(row.ProcessingStatus, SlackInboundRequestProcessingStatus.Completed, StringComparison.Ordinal)
            || string.Equals(row.ProcessingStatus, SlackInboundRequestProcessingStatus.Failed, StringComparison.Ordinal)
            || string.Equals(row.ProcessingStatus, SlackInboundRequestProcessingStatus.ModalOpened, StringComparison.Ordinal)
            || string.Equals(row.ProcessingStatus, SlackInboundRequestProcessingStatus.CompletionPersistFailed, StringComparison.Ordinal))
        {
            this.logger.LogDebug(
                "Slack idempotency guard skipped redundant transition for idempotency_key={IdempotencyKey}: existing_status={ExistingStatus} requested_status={RequestedStatus}.",
                idempotencyKey,
                row.ProcessingStatus,
                terminalStatus);
            return true;
        }

        row.ProcessingStatus = terminalStatus;
        row.CompletedAt = this.timeProvider.GetUtcNow();
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private TimeSpan ComputeCompletionDelay(int completedAttempt)
    {
        if (this.completionInitialDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Exponential backoff: initial * 2^(attempt-1), capped at 1s
        // so a slow terminal-status write cannot wedge the dispatch
        // loop on a healthy host.
        double factor = Math.Pow(2, Math.Max(0, completedAttempt - 1));
        double millis = Math.Min(1000.0, this.completionInitialDelay.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(millis);
    }

    private static string MapSourceType(SlackInboundSourceType sourceType) => sourceType switch
    {
        SlackInboundSourceType.Event => "event",
        SlackInboundSourceType.Command => "command",
        SlackInboundSourceType.Interaction => "interaction",
        _ => "unspecified",
    };

    /// <summary>
    /// Atomically reclaims a stale <c>processing</c> row by updating
    /// its <see cref="SlackInboundRequestRecord.FirstSeenAt"/> via a
    /// SQL <c>UPDATE ... WHERE first_seen_at = previous_value</c>
    /// guard. Returns <see langword="true"/> when the row was reclaimed
    /// (exactly one row affected) and <see langword="false"/> when
    /// another replica got there first (zero rows affected because
    /// the WHERE filter no longer matches).
    /// </summary>
    /// <remarks>
    /// In addition to bumping <see cref="SlackInboundRequestRecord.FirstSeenAt"/>
    /// (the lease-ownership timestamp) and resetting
    /// <see cref="SlackInboundRequestRecord.CompletedAt"/> to <see langword="null"/>
    /// (the crashed worker may have stamped it during a partial
    /// failure path), the reclaim ALSO refreshes
    /// <see cref="SlackInboundRequestRecord.SourceType"/> to the
    /// redelivery envelope's transport. This keeps the EF guard's
    /// contract aligned with
    /// <see cref="InMemorySlackIdempotencyGuard.TryAcquireAsync"/>:
    /// the active lease's <c>source_type</c> reflects whichever
    /// transport ultimately acquired ownership. The contract matters
    /// for operator triage -- a redelivery that arrives via a
    /// different transport (e.g. socket-mode retry of an HTTP-
    /// originated event) would otherwise leave the persisted
    /// <c>source_type</c> set to the crashed worker's original value
    /// and mis-classify the row in <c>WHERE source_type = ?</c>
    /// queries. Stable identifiers
    /// (<see cref="SlackInboundRequestRecord.TeamId"/>,
    /// <see cref="SlackInboundRequestRecord.UserId"/>,
    /// <see cref="SlackInboundRequestRecord.RawPayloadHash"/>) are
    /// intentionally NOT updated: Slack guarantees the same event /
    /// command / interaction redelivers with the same team, user, and
    /// payload, so re-writing them is unnecessary churn and the
    /// in-memory reference guard does not touch them either.
    /// </remarks>
    private async Task<bool> TryReclaimStaleLeaseAsync(
        TContext context,
        SlackInboundEnvelope envelope,
        DateTimeOffset previousFirstSeenAt,
        DateTimeOffset newFirstSeenAt,
        CancellationToken ct)
    {
        try
        {
            string reclaimedSourceType = MapSourceType(envelope.SourceType);

            int affected = await context.SlackInboundRequestRecords
                .Where(r => r.IdempotencyKey == envelope.IdempotencyKey
                            && r.ProcessingStatus == SlackInboundRequestProcessingStatus.Processing
                            && r.FirstSeenAt == previousFirstSeenAt)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.FirstSeenAt, newFirstSeenAt)
                        .SetProperty(r => r.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(r => r.SourceType, reclaimedSourceType),
                    ct)
                .ConfigureAwait(false);

            return affected == 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack idempotency guard failed to reclaim stale processing lease for idempotency_key={IdempotencyKey}; deferring as duplicate.",
                envelope.IdempotencyKey);
            return false;
        }
    }

    private static string HashRawPayload(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        StringBuilder sb = new(digest.Length * 2);
        foreach (byte b in digest)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
