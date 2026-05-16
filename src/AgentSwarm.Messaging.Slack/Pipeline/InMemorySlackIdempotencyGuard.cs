// -----------------------------------------------------------------------
// <copyright file="InMemorySlackIdempotencyGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Options;

/// <summary>
/// In-process <see cref="ISlackIdempotencyGuard"/> intended for tests
/// and developer-laptop hosts that do not wire an EF Core
/// <see cref="Persistence.ISlackInboundRequestRecordDbContext"/>. The
/// in-memory dictionary lives for the process lifetime: it does NOT
/// survive a restart, so it must NOT be used in production.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Tests use this guard to exercise the ingestor pipeline without
/// standing up a SQLite context; the canonical durable guard is
/// <see cref="SlackIdempotencyGuard{TContext}"/>.
/// </para>
/// <para>
/// Behaviour mirrors the EF guard's contract:
/// <see cref="TryAcquireAsync"/> applies the architecture.md §2.6
/// lease semantics -- a brand-new key acquires, a terminal /
/// fast-path row reports a true duplicate, a recent
/// <c>processing</c> row defers to the in-flight worker, and a
/// stale <c>processing</c> row is reclaimed via CAS so a crashed
/// worker's lease does not block future Slack retries forever.
/// <see cref="MarkCompletedAsync"/> /
/// <see cref="MarkFailedAsync"/> flip the recorded status. Missing
/// rows are tolerated silently so the API matches the durable guard's
/// "do not crash the ingestor on the unhappy path" contract.
/// </para>
/// </remarks>
internal sealed class InMemorySlackIdempotencyGuard : ISlackIdempotencyGuard
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan staleProcessingThreshold;

    public InMemorySlackIdempotencyGuard(
        TimeProvider? timeProvider = null,
        IOptions<SlackConnectorOptions>? options = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        int seconds = options?.Value?.Idempotency?.StaleProcessingThresholdSeconds
            ?? new SlackIdempotencyOptions().StaleProcessingThresholdSeconds;
        this.staleProcessingThreshold = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    /// <summary>
    /// Snapshot accessor exposed to tests so they can assert the
    /// recorded processing-status without reaching into a database.
    /// Keys are idempotency keys, values are
    /// <c>(SourceType, ProcessingStatus, CompletedAt)</c>.
    /// </summary>
    internal IReadOnlyDictionary<string, Entry> Snapshot
        => new Dictionary<string, Entry>(this.entries, StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrEmpty(envelope.IdempotencyKey))
        {
            throw new ArgumentException(
                "SlackInboundEnvelope.IdempotencyKey must be populated before invoking the idempotency guard.",
                nameof(envelope));
        }

        ct.ThrowIfCancellationRequested();

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        DateTimeOffset firstSeenAtForFresh = envelope.ReceivedAt == default ? now : envelope.ReceivedAt;
        Entry fresh = new(
            SourceType: envelope.SourceType,
            ProcessingStatus: SlackInboundRequestProcessingStatus.Processing,
            FirstSeenAt: firstSeenAtForFresh,
            CompletedAt: null);

        // First, try to insert a brand-new row. TryAdd returns true
        // iff the key was absent, which is the common happy path.
        if (this.entries.TryAdd(envelope.IdempotencyKey, fresh))
        {
            return Task.FromResult(true);
        }

        // A row exists. Apply the architecture.md §2.6 lease
        // semantics: terminal / fast-path rows AND a recent
        // 'processing' row both return false (true duplicate vs.
        // deferred live lease respectively -- the audit row shares
        // the 'duplicate' outcome marker but the existing row's
        // status disambiguates them). A stale 'processing' row is
        // reclaimed via CAS below so a crashed worker's lease does
        // not block future Slack retries forever.
        if (!this.entries.TryGetValue(envelope.IdempotencyKey, out Entry existing))
        {
            // Lost the row between TryAdd and TryGetValue -- retry add.
            bool acquiredOnRetry = this.entries.TryAdd(envelope.IdempotencyKey, fresh);
            return Task.FromResult(acquiredOnRetry);
        }

        if (string.Equals(existing.ProcessingStatus, SlackInboundRequestProcessingStatus.Processing, StringComparison.Ordinal))
        {
            TimeSpan age = now - existing.FirstSeenAt;
            if (age >= this.staleProcessingThreshold)
            {
                Entry reclaimed = existing with
                {
                    FirstSeenAt = now,
                    CompletedAt = null,
                    SourceType = envelope.SourceType,
                };

                // CAS reclaim: only succeeds if the row is still the
                // one we observed (i.e. nobody else reclaimed first).
                if (this.entries.TryUpdate(envelope.IdempotencyKey, reclaimed, existing))
                {
                    return Task.FromResult(true);
                }
            }
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task MarkCompletedAsync(string idempotencyKey, CancellationToken ct)
        => this.UpdateTerminalStatusAsync(idempotencyKey, SlackInboundRequestProcessingStatus.Completed, ct);

    /// <inheritdoc />
    public Task MarkFailedAsync(string idempotencyKey, CancellationToken ct)
        => this.UpdateTerminalStatusAsync(idempotencyKey, SlackInboundRequestProcessingStatus.Failed, ct);

    /// <summary>
    /// Pre-populates a row in the in-memory store -- used by tests
    /// that want to simulate "Slack retry arrives after the original
    /// processing completed" without first running the happy path.
    /// </summary>
    internal void Preload(string idempotencyKey, string processingStatus)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(processingStatus);

        this.entries[idempotencyKey] = new Entry(
            SourceType: SlackInboundSourceType.Unspecified,
            ProcessingStatus: processingStatus,
            FirstSeenAt: this.timeProvider.GetUtcNow(),
            CompletedAt: string.Equals(processingStatus, SlackInboundRequestProcessingStatus.Processing, StringComparison.Ordinal)
                ? null
                : this.timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Pre-populates a row with a caller-supplied <paramref name="firstSeenAt"/>
    /// so tests can age a <c>processing</c> lease past the stale-reclaim
    /// threshold without manipulating wall-clock time.
    /// </summary>
    internal void Preload(string idempotencyKey, string processingStatus, DateTimeOffset firstSeenAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(processingStatus);

        this.entries[idempotencyKey] = new Entry(
            SourceType: SlackInboundSourceType.Unspecified,
            ProcessingStatus: processingStatus,
            FirstSeenAt: firstSeenAt,
            CompletedAt: string.Equals(processingStatus, SlackInboundRequestProcessingStatus.Processing, StringComparison.Ordinal)
                ? null
                : firstSeenAt);
    }

    private Task UpdateTerminalStatusAsync(string idempotencyKey, string terminalStatus, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();

        // Mirror the EF guard's tolerance: a missing row is logged
        // (here: silently dropped) rather than throwing so a transient
        // mismatch between TryAcquireAsync and MarkCompleted/Failed
        // cannot crash the ingestor's dispatch loop.
        if (!this.entries.TryGetValue(idempotencyKey, out Entry existing))
        {
            return Task.CompletedTask;
        }

        if (string.Equals(existing.ProcessingStatus, SlackInboundRequestProcessingStatus.Completed, StringComparison.Ordinal)
            || string.Equals(existing.ProcessingStatus, SlackInboundRequestProcessingStatus.Failed, StringComparison.Ordinal)
            || string.Equals(existing.ProcessingStatus, SlackInboundRequestProcessingStatus.ModalOpened, StringComparison.Ordinal)
            || string.Equals(existing.ProcessingStatus, SlackInboundRequestProcessingStatus.CompletionPersistFailed, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        Entry updated = existing with
        {
            ProcessingStatus = terminalStatus,
            CompletedAt = this.timeProvider.GetUtcNow(),
        };

        // TryUpdate uses CAS semantics, so a concurrent transition
        // (e.g., two tests racing) cannot overwrite a different
        // terminal status we did not observe.
        this.entries.TryUpdate(idempotencyKey, updated, existing);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Snapshot row recorded by the in-memory guard. Mirrors the EF
    /// <see cref="Entities.SlackInboundRequestRecord"/> subset that
    /// tests actually assert against.
    /// </summary>
    internal readonly record struct Entry(
        SlackInboundSourceType SourceType,
        string ProcessingStatus,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset? CompletedAt);
}
