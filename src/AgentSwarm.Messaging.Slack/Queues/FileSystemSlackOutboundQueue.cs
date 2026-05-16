// -----------------------------------------------------------------------
// <copyright file="FileSystemSlackOutboundQueue.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Queues;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable disk-backed implementation of
/// <see cref="IAcknowledgeableSlackOutboundQueue"/> that journals
/// every enqueued envelope to a dedicated file under a configurable
/// directory so outbound messages survive process restart
/// (FR-005 durable outbound queue / FR-007 zero message loss /
/// connector restart recovery).
/// </summary>
/// <remarks>
/// <para>
/// Stage 6.3 evaluator iter-1 item #2: the Worker's canonical wiring
/// must not default to a non-durable
/// <see cref="ChannelBasedSlackOutboundQueue"/>. This implementation
/// pairs with <see cref="FileSystemSlackDeadLetterQueue"/> so both
/// queue surfaces persist envelopes through restart, and the worker
/// composition root replaces the in-memory defaults via
/// <see cref="FileSystemSlackOutboundQueueServiceCollectionExtensions.AddFileSystemSlackOutboundQueue"/>.
/// </para>
/// <para>
/// <b>Storage shape</b>. One file per pending envelope under
/// <c>{directoryPath}/pending/</c>. File name is
/// <c>{sortable-utc-timestamp}-{guid}.json</c> so a directory
/// listing yields FIFO order, the same order in which envelopes
/// were originally produced. File contents are a JSON object
/// containing the persisted shape of
/// <see cref="SlackOutboundEnvelope"/>. On
/// <see cref="AcknowledgeAsync"/> the file is deleted; on next
/// startup the directory is replayed in name order, re-pushing
/// every unacknowledged entry into the in-memory channel for the
/// dispatcher.
/// </para>
/// <para>
/// <b>At-least-once semantics</b>. Each envelope is durably written
/// to disk BEFORE being made visible to the dispatcher (the channel
/// write follows the file write); a crash before
/// <see cref="AcknowledgeAsync"/> means the file remains and is
/// replayed on next start. Duplicate delivery on replay is
/// tolerated -- Slack's own idempotency model and the outbound
/// dispatcher's success path absorb a duplicate POST. Message loss
/// is not tolerated.
/// </para>
/// <para>
/// <b>EnvelopeId-keyed ack tracking</b>. The implementation tracks
/// in-flight envelopes via a <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// keyed by <see cref="SlackOutboundEnvelope.EnvelopeId"/> (a stable
/// per-envelope <see cref="System.Guid"/> assigned at enqueue time
/// and persisted in the journal record). This survives the record
/// value-equality and <c>with</c>-expression copy semantics that
/// would defeat a reference-comparer-keyed map, and lets ack lookups
/// continue to find the right journal entry after process restart's
/// replay re-creates the envelope instance from disk.
/// </para>
/// <para>
/// <b>Injected clock</b>. The sortable-timestamp prefix of the
/// journal file name is sourced from an injected
/// <see cref="TimeProvider"/> -- the same testability pattern used
/// by <see cref="Pipeline.SlackOutboundDispatcher"/>,
/// <see cref="Pipeline.SlackTokenBucketRateLimiter"/>, and
/// <see cref="Pipeline.HttpClientSlackOutboundDispatchClient"/>. The
/// production no-arg path delegates to <see cref="TimeProvider.System"/>;
/// the internal constructor lets unit tests pin the clock and assert
/// deterministic journal file ordering across same-millisecond
/// enqueues without relying on wall-clock races.
/// </para>
/// <para>
/// <b>Cancellation escape hatch</b>. The single-slot
/// <c>writeGate</c> serialises journal writes. Without an escape
/// hatch, a stuck I/O operation (full disk, dropped network share,
/// hung file handle) would block every subsequent
/// <see cref="EnqueueAsync(SlackOutboundEnvelope)"/> call
/// indefinitely. Two complementary mechanisms address this:
/// (1) the queue holds a <see cref="CancellationTokenSource"/>
/// (<c>lifetimeCts</c>) that <see cref="Dispose"/> cancels, and the
/// linked token is passed to every <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
/// call, so disposing the queue (during host shutdown) wakes any
/// gate-waiter; (2) callers holding a concrete reference may use
/// the <see cref="EnqueueAsync(SlackOutboundEnvelope, CancellationToken)"/>
/// overload to supply a per-call token (e.g. a host stopping
/// token). The <see cref="ISlackOutboundQueue"/> interface
/// intentionally omits a per-call cancellation token (Stage 1.3
/// contract: producers have handed off ownership and cannot retract
/// a queued send); the overload satisfies that contract's
/// "implementations that require per-call cancellation expose it on
/// their concrete type" guidance.
/// </para>
/// </remarks>
internal sealed class FileSystemSlackOutboundQueue : IAcknowledgeableSlackOutboundQueue, IDisposable
{
    /// <summary>Default child directory holding pending envelopes.</summary>
    public const string PendingDirectoryName = "pending";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<FileSystemSlackOutboundQueue> logger;
    private readonly TimeProvider timeProvider;
    private readonly Channel<SlackOutboundEnvelope> backing;
    private readonly ConcurrentDictionary<Guid, string> inFlightFilePaths;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    // Cancelled by Dispose so a stuck I/O operation cannot wedge the
    // writeGate past the queue's lifetime. Every WaitAsync below uses
    // a token linked to this source -- giving the interface-shaped
    // EnqueueAsync(envelope) overload (which carries no per-call CT
    // by Stage 1.3 contract) at least a shutdown-time escape hatch.
    private readonly CancellationTokenSource lifetimeCts = new();
    private bool disposed;

    /// <summary>
    /// Production constructor: uses <see cref="TimeProvider.System"/>
    /// as the wall clock so hosts get monotonic UTC timestamps in
    /// journal file names. Delegates to the internal
    /// clock-injecting overload so the implementation lives in one
    /// place.
    /// </summary>
    public FileSystemSlackOutboundQueue(
        string directoryPath,
        ILogger<FileSystemSlackOutboundQueue> logger)
        : this(directoryPath, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets the fixture inject a fake
    /// <see cref="TimeProvider"/> so the sortable-timestamp prefix
    /// of journal file names is deterministic. Used by the durable
    /// queue's unit tests to assert FIFO replay ordering across
    /// same-millisecond enqueues without relying on wall-clock races.
    /// </summary>
    internal FileSystemSlackOutboundQueue(
        string directoryPath,
        ILogger<FileSystemSlackOutboundQueue> logger,
        TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Outbound-queue directory path must be supplied.", nameof(directoryPath));
        }

        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.AbsoluteDirectoryPath = Path.GetFullPath(directoryPath);
        this.PendingDirectoryPath = Path.Combine(this.AbsoluteDirectoryPath, PendingDirectoryName);

        // Eager directory creation surfaces permission / path errors
        // at composition time rather than during the first send.
        Directory.CreateDirectory(this.PendingDirectoryPath);

        this.backing = Channel.CreateUnbounded<SlackOutboundEnvelope>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        this.inFlightFilePaths = new ConcurrentDictionary<Guid, string>();

        this.ReplayUnacknowledgedFromDisk();
    }

    /// <summary>Gets the absolute path to the root queue directory.</summary>
    public string AbsoluteDirectoryPath { get; }

    /// <summary>Gets the absolute path to the pending-entries sub-directory.</summary>
    public string PendingDirectoryPath { get; }

    /// <summary>Internal test affordance: number of envelopes currently un-acked on disk.</summary>
    internal int PendingFileCount =>
        Directory.Exists(this.PendingDirectoryPath)
            ? Directory.GetFiles(this.PendingDirectoryPath, "*.json").Length
            : 0;

    /// <inheritdoc />
    /// <remarks>
    /// Contract-shaped overload required by <see cref="ISlackOutboundQueue"/>
    /// -- the Stage 1.3 brief intentionally omits a per-call
    /// cancellation token on the interface so producers cannot
    /// retract a queued send. Delegates to the concrete-typed
    /// <see cref="EnqueueAsync(SlackOutboundEnvelope, CancellationToken)"/>
    /// overload with <see cref="CancellationToken.None"/>, which
    /// still links to <c>lifetimeCts</c> internally so a stuck I/O
    /// operation cannot pin the write gate past <see cref="Dispose"/>.
    /// </remarks>
    public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope) =>
        this.EnqueueAsync(envelope, CancellationToken.None);

    /// <summary>
    /// Concrete-typed overload that lets callers supply a per-call
    /// <see cref="CancellationToken"/> (e.g. a host stopping token)
    /// to abort an in-flight enqueue. Exposed only on the concrete
    /// type per the <see cref="ISlackOutboundQueue"/> contract
    /// guidance ("implementations that require per-call cancellation
    /// expose it on their concrete type, not on this interface").
    /// The supplied token is linked with the queue's lifetime token
    /// so disposing the queue also wakes the wait regardless of what
    /// the caller passed -- this is the cancellation escape hatch
    /// the contract-shaped overload relies on.
    /// </summary>
    public async ValueTask EnqueueAsync(SlackOutboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.ThrowIfDisposed();

        // Sortable filename so directory enumeration yields FIFO order
        // on replay. The leading sortable UTC tick prefix preserves
        // ordering between same-millisecond enqueues; the EnvelopeId
        // suffix gives us a stable per-envelope handle for ack
        // (deletion) without having to reopen the file to read its
        // contents. The clock is sourced from the injected
        // TimeProvider so unit tests can pin ordering deterministically
        // -- matching the testability pattern used by the dispatcher,
        // rate limiter, and dispatch client in this stage.
        long utcTicks = this.timeProvider.GetUtcNow().UtcDateTime.Ticks;
        string fileName = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{utcTicks:D19}-{envelope.EnvelopeId:N}.json");
        string filePath = Path.Combine(this.PendingDirectoryPath, fileName);

        FileSystemSlackOutboundRecord record = ToRecord(envelope);
        string json = JsonSerializer.Serialize(record, JsonOptions);

        // Link the caller's token with the queue's lifetime token so
        // both shutdown (Dispose -> lifetimeCts.Cancel) and per-call
        // cancellation wake a stuck gate-waiter. The linked source is
        // cheap; we always create it so the code path is uniform
        // regardless of whether `ct` is None.
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(this.lifetimeCts.Token, ct);

        await this.writeGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            // Write to a temp file then atomically rename so a crash
            // mid-write does not leave a partial JSON line that the
            // replay parser would have to skip. File.Move with
            // overwrite=false is atomic on the same volume.
            string tempPath = filePath + ".tmp";
            await using (FileStream stream = new(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, linkedCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);
            }

            File.Move(tempPath, filePath, overwrite: false);
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Cancellation is a first-class outcome -- do NOT wrap it
            // in SlackOutboundQueuePersistenceException (that exception
            // is reserved for genuine durability failures that the
            // producer must surface upstream). Propagate so the caller
            // sees the OCE and the channel publish below is skipped.
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogCritical(
                ex,
                "FileSystemSlackOutboundQueue FAILED to persist outbound envelope task_id={TaskId} correlation_id={CorrelationId} to {FilePath}.",
                envelope.TaskId,
                envelope.CorrelationId,
                filePath);

            // Re-throw so the producer (SlackConnector) sees the
            // failure and can surface it upstream. We do NOT silently
            // hand the envelope to the channel without a journal
            // backing -- that would re-introduce the zero-message-loss
            // bug the durable queue is meant to fix.
            throw new SlackOutboundQueuePersistenceException(this.PendingDirectoryPath, envelope, ex);
        }
        finally
        {
            this.writeGate.Release();
        }

        // Only publish to the in-memory channel AFTER the durable
        // journal write succeeds.
        this.inFlightFilePaths[envelope.EnvelopeId] = filePath;
        await this.backing.Writer.WriteAsync(envelope, linkedCts.Token).ConfigureAwait(false);

        this.logger.LogDebug(
            "FileSystemSlackOutboundQueue enqueued envelope task_id={TaskId} correlation_id={CorrelationId} envelope_id={EnvelopeId} file={FilePath}.",
            envelope.TaskId,
            envelope.CorrelationId,
            envelope.EnvelopeId,
            filePath);
    }

    /// <inheritdoc />
    public ValueTask<SlackOutboundEnvelope> DequeueAsync(CancellationToken ct)
    {
        return this.backing.Reader.ReadAsync(ct);
    }

    /// <inheritdoc />
    public async Task AcknowledgeAsync(SlackOutboundEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (!this.inFlightFilePaths.TryRemove(envelope.EnvelopeId, out string? filePath))
        {
            // Either the envelope did not originate here (e.g. test
            // double passed the wrong instance) or it was already
            // acked. Either way, nothing to do.
            return;
        }

        // Same linkage rationale as EnqueueAsync: dispose-time
        // cancellation must also unblock a gate-waiter on the ack
        // path even when the caller's token is None.
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(this.lifetimeCts.Token, ct);

        await this.writeGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            // Re-stage the in-flight tracking so a future ack can
            // retry deletion (defensive -- in practice the dispatcher
            // does not retry; the file will be re-deleted on the next
            // process restart's replay path which double-ack-deletes
            // anyway).
            this.inFlightFilePaths[envelope.EnvelopeId] = filePath;
            this.logger.LogWarning(
                ex,
                "FileSystemSlackOutboundQueue failed to delete acknowledged journal entry task_id={TaskId} correlation_id={CorrelationId} envelope_id={EnvelopeId} file={FilePath}. The entry will replay on next restart; the dispatcher's idempotent dispatch path absorbs the duplicate.",
                envelope.TaskId,
                envelope.CorrelationId,
                envelope.EnvelopeId,
                filePath);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.backing.Writer.TryComplete();

        // Cancel BEFORE disposing the gate / CTS so any pending
        // WaitAsync(linkedCts.Token) callers wake with an
        // OperationCanceledException rather than racing into an
        // ObjectDisposedException from the underlying SemaphoreSlim.
        try
        {
            this.lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a concurrent Dispose race -- ignore.
        }

        this.writeGate.Dispose();
        this.lifetimeCts.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(FileSystemSlackOutboundQueue));
        }
    }

    private void ReplayUnacknowledgedFromDisk()
    {
        if (!Directory.Exists(this.PendingDirectoryPath))
        {
            return;
        }

        // Sort by file name so replay preserves the original enqueue
        // order (the name's leading tick prefix is monotonic for the
        // life of the producing process).
        string[] files;
        try
        {
            files = Directory.GetFiles(this.PendingDirectoryPath, "*.json");
        }
        catch (Exception ex)
        {
            this.logger.LogCritical(
                ex,
                "FileSystemSlackOutboundQueue FAILED to enumerate pending journal directory {Directory}; outbound replay skipped. Operator must triage manually.",
                this.PendingDirectoryPath);
            return;
        }

        Array.Sort(files, StringComparer.Ordinal);

        int replayed = 0;
        foreach (string filePath in files)
        {
            SlackOutboundEnvelope? envelope = this.TryReadJournalEntry(filePath);
            if (envelope is null)
            {
                // The skip is logged inside TryReadJournalEntry; we
                // intentionally do NOT delete the file so an operator
                // can recover it manually.
                continue;
            }

            this.inFlightFilePaths[envelope.EnvelopeId] = filePath;
            if (!this.backing.Writer.TryWrite(envelope))
            {
                // The unbounded channel always accepts; failing here
                // means the channel was completed (during shutdown),
                // which should not happen during construction.
                this.logger.LogWarning(
                    "FileSystemSlackOutboundQueue could not replay journal entry {FilePath} -- channel rejected write.",
                    filePath);
                continue;
            }

            replayed++;
        }

        if (replayed > 0)
        {
            this.logger.LogInformation(
                "FileSystemSlackOutboundQueue replayed {ReplayedCount} unacknowledged outbound envelope(s) from {Directory}.",
                replayed,
                this.PendingDirectoryPath);
        }
    }

    private SlackOutboundEnvelope? TryReadJournalEntry(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            FileSystemSlackOutboundRecord? record =
                JsonSerializer.Deserialize<FileSystemSlackOutboundRecord>(json, JsonOptions);
            if (record is null)
            {
                this.logger.LogWarning(
                    "FileSystemSlackOutboundQueue skipped journal entry {FilePath} -- deserialized to null. The file will remain on disk for operator triage.",
                    filePath);
                return null;
            }

            return FromRecord(record);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "FileSystemSlackOutboundQueue skipped malformed journal entry {FilePath}. The file will remain on disk for operator triage.",
                filePath);
            return null;
        }
    }

    private static FileSystemSlackOutboundRecord ToRecord(SlackOutboundEnvelope envelope) => new(
        EnvelopeId: envelope.EnvelopeId,
        TaskId: envelope.TaskId,
        CorrelationId: envelope.CorrelationId,
        MessageType: envelope.MessageType.ToString(),
        BlockKitPayload: envelope.BlockKitPayload,
        ThreadTs: envelope.ThreadTs,
        MessageTs: envelope.MessageTs,
        ViewId: envelope.ViewId);

    private static SlackOutboundEnvelope FromRecord(FileSystemSlackOutboundRecord record)
    {
        SlackOutboundOperationKind op = Enum.TryParse(record.MessageType, out SlackOutboundOperationKind parsed)
            ? parsed
            : SlackOutboundOperationKind.PostMessage;
        return new SlackOutboundEnvelope(
            TaskId: record.TaskId,
            CorrelationId: record.CorrelationId,
            MessageType: op,
            BlockKitPayload: record.BlockKitPayload,
            ThreadTs: record.ThreadTs)
        {
            // Preserve the original EnvelopeId across restart so the
            // dispatcher's downstream ack lands on the same journal
            // entry. Legacy on-disk records without the field
            // (default Guid.Empty) get a fresh id -- the entry will
            // still ack-delete because we key the in-flight map by
            // the replay-time EnvelopeId.
            EnvelopeId = record.EnvelopeId == Guid.Empty ? Guid.NewGuid() : record.EnvelopeId,
            MessageTs = record.MessageTs,
            ViewId = record.ViewId,
        };
    }

    /// <summary>Persisted shape of <see cref="SlackOutboundEnvelope"/>.</summary>
    internal sealed record FileSystemSlackOutboundRecord(
        Guid EnvelopeId,
        string TaskId,
        string CorrelationId,
        string MessageType,
        string BlockKitPayload,
        string? ThreadTs,
        string? MessageTs = null,
        string? ViewId = null);
}

/// <summary>
/// Raised when the file-system journal write inside
/// <see cref="FileSystemSlackOutboundQueue.EnqueueAsync(SlackOutboundEnvelope)"/> fails.
/// The wrapped IO exception (typically full disk, permission denied,
/// network share dropped) is preserved on
/// <see cref="Exception.InnerException"/> so operators can triage.
/// Propagating instead of swallowing preserves the FR-005 / FR-007
/// zero-message-loss guarantee: the producer (SlackConnector) sees
/// the failure and can surface it upstream rather than the channel
/// silently buffering a message that has no durable backing.
/// </summary>
[Serializable]
internal sealed class SlackOutboundQueuePersistenceException : Exception
{
    public SlackOutboundQueuePersistenceException(string directoryPath, SlackOutboundEnvelope envelope, Exception inner)
        : base($"Failed to persist outbound envelope task_id={envelope?.TaskId ?? "<null>"} correlation_id={envelope?.CorrelationId ?? "<null>"} to {directoryPath}.", inner)
    {
        this.DirectoryPath = directoryPath ?? throw new ArgumentNullException(nameof(directoryPath));
        this.Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
    }

    public string DirectoryPath { get; }

    public SlackOutboundEnvelope Envelope { get; }
}

/// <summary>
/// DI extensions for <see cref="FileSystemSlackOutboundQueue"/>.
/// </summary>
public static class FileSystemSlackOutboundQueueServiceCollectionExtensions
{
    /// <summary>
    /// Replaces <see cref="ISlackOutboundQueue"/> with the durable
    /// disk-backed implementation rooted at
    /// <paramref name="directoryPath"/>. Hosts call this BEFORE
    /// <c>AddSlackOutboundDispatcher</c> so the dispatcher
    /// extension's <c>TryAddSingleton</c> for the in-memory channel
    /// queue is a no-op.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="directoryPath">
    /// Absolute or relative path to the directory the journal files
    /// live in. The directory and its <c>pending</c> sub-directory
    /// are created eagerly if missing.
    /// </param>
    /// <remarks>
    /// The queue resolves its <see cref="TimeProvider"/> from DI when
    /// one is registered (matching the dispatcher / rate-limiter /
    /// dispatch-client wiring); otherwise it falls back to
    /// <see cref="TimeProvider.System"/>. This keeps the production
    /// no-config path working while letting integration test hosts
    /// substitute a fake clock to assert deterministic journal
    /// file ordering.
    /// </remarks>
    public static IServiceCollection AddFileSystemSlackOutboundQueue(
        this IServiceCollection services,
        string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Outbound-queue directory path must be supplied.", nameof(directoryPath));
        }

        services.RemoveAll<ISlackOutboundQueue>();
        services.AddSingleton<FileSystemSlackOutboundQueue>(sp =>
            new FileSystemSlackOutboundQueue(
                directoryPath,
                sp.GetRequiredService<ILogger<FileSystemSlackOutboundQueue>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<ISlackOutboundQueue>(sp =>
            sp.GetRequiredService<FileSystemSlackOutboundQueue>());

        return services;
    }
}
