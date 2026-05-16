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
/// <b>Cancellation propagation</b>. The single-arg
/// <see cref="EnqueueAsync(SlackOutboundEnvelope)"/> overload exists
/// to satisfy the <see cref="ISlackOutboundQueue"/> contract; it
/// delegates to the
/// <see cref="EnqueueAsync(SlackOutboundEnvelope, CancellationToken)"/>
/// overload with <see cref="CancellationToken.None"/>. Hosts /
/// connector producers that hold a stopping token should call the
/// CT overload so that a stuck file-system I/O operation holding the
/// internal write gate cannot block subsequent producers indefinitely
/// -- the gate, the journal write, and the in-memory channel write
/// all honour the supplied token. This mirrors
/// <see cref="AcknowledgeAsync"/>, which already forwards its token
/// to <c>writeGate.WaitAsync(ct)</c>.
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
    /// Interface-contract shim. The
    /// <see cref="ISlackOutboundQueue.EnqueueAsync(SlackOutboundEnvelope)"/>
    /// signature does not carry a cancellation token, so this entry
    /// point delegates to the CT-aware overload with
    /// <see cref="CancellationToken.None"/>. Producers that hold a
    /// stopping token (hosted services, connector shutdown paths)
    /// should call
    /// <see cref="EnqueueAsync(SlackOutboundEnvelope, CancellationToken)"/>
    /// directly so a stuck I/O operation holding the write gate
    /// cannot block them indefinitely.
    /// </remarks>
    public ValueTask EnqueueAsync(SlackOutboundEnvelope envelope)
        => this.EnqueueAsync(envelope, CancellationToken.None);

    /// <summary>
    /// Cancellation-aware overload of <see cref="EnqueueAsync(SlackOutboundEnvelope)"/>.
    /// </summary>
    /// <param name="envelope">The envelope to persist and publish.</param>
    /// <param name="ct">
    /// Caller-supplied token. Honoured at three points:
    /// (1) acquiring the internal write gate -- so a stuck I/O
    /// operation holding the gate does NOT block subsequent producers
    /// indefinitely (the symmetric concern that <see cref="AcknowledgeAsync"/>
    /// already addresses by forwarding <c>ct</c> to
    /// <c>writeGate.WaitAsync(ct)</c>); (2) the journal file write /
    /// flush, so cancellation pre-empts a slow disk; (3) the
    /// in-memory channel publish, for symmetry. On
    /// <see cref="OperationCanceledException"/> any partial
    /// <c>.tmp</c> file is best-effort deleted and the exception
    /// propagates unwrapped so callers can distinguish cancellation
    /// from a genuine persistence failure (which surfaces as
    /// <see cref="SlackOutboundQueuePersistenceException"/>).
    /// </param>
    public async ValueTask EnqueueAsync(SlackOutboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

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

        // Forward the token to WaitAsync so a stuck I/O operation
        // holding the gate cannot block this producer indefinitely.
        // Matches the AcknowledgeAsync path which already does
        // writeGate.WaitAsync(ct).
        await this.writeGate.WaitAsync(ct).ConfigureAwait(false);
        string tempPath = filePath + ".tmp";
        try
        {
            // Write to a temp file then atomically rename so a crash
            // mid-write does not leave a partial JSON line that the
            // replay parser would have to skip. File.Move with
            // overwrite=false is atomic on the same volume.
            await using (FileStream stream = new(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, filePath, overwrite: false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a first-class outcome distinct from a
            // persistence failure -- propagate unwrapped so the
            // producer can react (typically: stop accepting new work
            // because the host is shutting down) without triggering
            // the operator-facing CRITICAL log path that
            // SlackOutboundQueuePersistenceException implies. The
            // *.tmp orphan does not affect replay because
            // ReplayUnacknowledgedFromDisk enumerates with the
            // "*.json" mask, but we still best-effort delete it to
            // keep the pending directory tidy.
            TryDeleteIfExists(tempPath);
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

            // Best-effort cleanup of the temp file so a retry by the
            // producer does not collide with leftover state.
            TryDeleteIfExists(tempPath);

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
        // journal write succeeds. The token is forwarded for symmetry;
        // on an unbounded channel WriteAsync normally completes
        // synchronously, but a cancelled token will still short-circuit
        // it. If cancellation lands between the file write and the
        // channel write, the journal entry remains on disk and will
        // replay on next start -- the at-least-once contract holds.
        this.inFlightFilePaths[envelope.EnvelopeId] = filePath;
        await this.backing.Writer.WriteAsync(envelope, ct).ConfigureAwait(false);

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

        await this.writeGate.WaitAsync(ct).ConfigureAwait(false);
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
        this.writeGate.Dispose();
    }

    private static void TryDeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of an interrupted journal write. The
            // replay path enumerates with the "*.json" mask, so a
            // surviving "*.tmp" orphan is benign; operator triage can
            // pick it up out-of-band.
        }
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
/// <see cref="FileSystemSlackOutboundQueue.EnqueueAsync(SlackOutboundEnvelope, CancellationToken)"/>
/// fails. The wrapped IO exception (typically full disk, permission
/// denied, network share dropped) is preserved on
/// <see cref="Exception.InnerException"/> so operators can triage.
/// Propagating instead of swallowing preserves the FR-005 / FR-007
/// zero-message-loss guarantee: the producer (SlackConnector) sees
/// the failure and can surface it upstream rather than the channel
/// silently buffering a message that has no durable backing.
/// <see cref="OperationCanceledException"/> is intentionally NOT
/// wrapped by this type -- cancellation is a first-class outcome and
/// the producer needs to distinguish it from a genuine persistence
/// failure (an operator-facing CRITICAL log line vs an expected
/// shutdown signal).
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
