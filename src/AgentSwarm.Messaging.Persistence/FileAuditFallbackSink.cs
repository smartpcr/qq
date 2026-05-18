// -----------------------------------------------------------------------
// <copyright file="FileAuditFallbackSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable file-backed <see cref="IAuditFallbackSink"/>. When
/// <see cref="PersistentAuditLogger.LogAsync"/> /
/// <c>LogHumanResponseAsync</c> throws (audit DB unreachable, schema
/// version skew, disk full on the DB host, network partition, ...),
/// the pipeline's denial-audit path enqueues the row here instead of
/// losing it. The file lives on the audit-process's local disk so a
/// total audit-DB outage cannot also remove the fallback target;
/// each line is a complete JSON document carrying the AuditEntry /
/// HumanResponseAuditEntry shape plus a <c>type</c> discriminator so
/// operator-side replay tooling can pick the correct
/// <see cref="IAuditLogger"/> overload when re-inserting the row
/// into <c>audit_logs</c> once the DB returns. Operator-side replay
/// is intentionally out of scope for Stage 5.3; Stage 5.3's contract
/// is durability of the row, and any standard JSONL-replay script
/// satisfies the recovery side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why JSON Lines (NDJSON), not CSV / SQLite / a structured
/// log.</b> Lines are atomic at the kernel level for writes &lt;
/// <c>PIPE_BUF</c> (4 KB on Linux, similar on Windows append
/// semantics), so multiple processes appending concurrently never
/// interleave bytes inside a single record. CSV would require quoting
/// rules and a header; SQLite would itself need a writer and reproduce
/// the very failure mode we are guarding against; a structured log
/// goes to the same logging sink the rest of the app uses and is
/// therefore vulnerable to the same outage. NDJSON is the smallest
/// durable primitive that survives independently of every other
/// infrastructure piece.
/// </para>
/// <para>
/// <b>Write discipline.</b> Each <see cref="EnqueueAsync(AuditEntry,CancellationToken)"/>
/// call opens the file in append-share-read mode, writes one line,
/// <see cref="FileStream.FlushAsync(System.Threading.CancellationToken)"/>'s
/// to disk with <c>flushToDisk: true</c> semantics
/// (<see cref="FileOptions.WriteThrough"/>), and closes. Open / append
/// / close per write is intentionally slower than holding a stream;
/// the fallback path is by definition the rare exception path, and
/// the durability guarantee (Stage 5.3 brief: "<i>log every inbound
/// command</i>") forbids buffering. Concurrent writers are
/// serialised on a <see cref="SemaphoreSlim"/> so a burst of
/// rejections during an audit-DB outage cannot lose a row to a
/// last-writer-wins file-handle race.
/// </para>
/// <para>
/// <b>Disk-fill guard.</b> A prolonged primary-audit outage at high
/// command throughput would, without a cap, grow this JSONL file
/// until the local disk is full — at which point the host process
/// itself crashes (any other file write — logs, tempfiles,
/// SQLite WAL — starts failing) and takes the primary audit path
/// down with it, removing the very recovery channel the file was
/// guarding. The sink therefore refuses to grow the file past
/// <see cref="MaxFileBytes"/> (default
/// <see cref="DefaultMaxFileBytes"/>, 100 MiB, configurable per
/// construction). When the cap is reached the offending write
/// throws <see cref="AuditFallbackCapacityExceededException"/> after
/// logging at <see cref="LogLevel.Critical"/>, which propagates to
/// the pipeline's outer Critical-on-fallback-failure escalation and
/// pages the operator BEFORE the disk fills. Recovery is operator-
/// driven (rotate / replay / truncate the file, then resume); a
/// self-rotating strategy is intentionally NOT layered in because
/// the matching replay tooling is per Stage 5.3 already operator-
/// driven, and adding rotated-file enumeration to that surface
/// would expand its blast radius without adding durability.
/// </para>
/// <para>
/// <b>Failure semantics.</b> If the fallback ALSO fails (the local
/// disk is full, the directory is read-only, the OS refuses the
/// handle, the size cap was reached, etc.) the sink rethrows so the
/// caller can decide. The pipeline's <c>WriteRejectionAuditAsync</c>
/// catches and logs the rethrown exception at
/// <see cref="LogLevel.Critical"/>, escalating the operator alert
/// beyond the warn-level the primary-only path emits — at that point
/// both audit storage layers have failed and a human MUST be paged.
/// The denial response itself still fires because the security-
/// critical user-facing reply is the higher-priority path; the
/// deferred alert is the recovery mechanism.
/// </para>
/// <para>
/// <b>Lifetime / disposal.</b> The sink owns a
/// <see cref="SemaphoreSlim"/> which lazily allocates a
/// <see cref="ManualResetEvent"/> — and therefore a kernel handle —
/// the first time <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
/// observes contention. Because the sink is normally registered as a
/// DI singleton, that handle would outlive the host on graceful
/// shutdown unless the container disposes the sink. The class
/// therefore implements <see cref="IDisposable"/>; the DI container
/// (or any host that constructs the sink manually) MUST dispose it
/// on shutdown to release the handle. <see cref="Dispose"/> is
/// idempotent and safe to call from any thread.
/// </para>
/// </remarks>
public sealed class FileAuditFallbackSink : IAuditFallbackSink, IDisposable
{
    /// <summary>
    /// Default file path used when the host does not configure one
    /// via <c>AuditDb:FallbackSinkPath</c>. The sibling-of-cwd
    /// location matches the SQLite default (<c>audit.db</c>) so
    /// dev / local hosts get a usable durable target without
    /// additional configuration.
    /// </summary>
    public const string DefaultRelativePath = "audit-fallback.jsonl";

    /// <summary>
    /// Default ceiling on the on-disk size of the fallback JSONL
    /// file, in bytes. 100 MiB. Chosen as the smallest value that
    /// (a) comfortably absorbs a multi-hour primary-DB outage at
    /// realistic per-row sizes (typical rejection row JSON ≈ 500
    /// bytes → ~200k rows fit before the cap) and (b) is small
    /// enough that local-disk free space on any plausibly
    /// provisioned host can absorb it without the host itself
    /// failing — so the cap fires before the disk does. Configurable
    /// per <see cref="FileAuditFallbackSink(string,ILogger{FileAuditFallbackSink},long)"/>.
    /// </summary>
    public const long DefaultMaxFileBytes = 100L * 1024L * 1024L;

    /// <summary>
    /// <c>type</c> discriminator literal for general-audit lines.
    /// Operator-side replay tooling consults this when re-inserting
    /// a fallback line into <c>audit_logs</c> so it can pick the
    /// correct <see cref="IAuditLogger"/> overload.
    /// </summary>
    public const string TypeGeneral = "general";

    /// <summary>
    /// <c>type</c> discriminator literal for human-response lines.
    /// See <see cref="TypeGeneral"/> remarks.
    /// </summary>
    public const string TypeHumanResponse = "human-response";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _filePath;
    private readonly ILogger<FileAuditFallbackSink> _logger;
    private readonly long _maxFileBytes;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private int _disposed;

    public FileAuditFallbackSink(
        string filePath,
        ILogger<FileAuditFallbackSink> logger,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "FileAuditFallbackSink requires a non-empty file path. Hosts that genuinely tolerate audit gaps must register NullAuditFallbackSink explicitly.",
                nameof(filePath));
        }

        if (maxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileBytes),
                maxFileBytes,
                "FileAuditFallbackSink size cap must be positive — a zero or negative cap would refuse the first write and silently drop every rejection audit row. Use DefaultMaxFileBytes (100 MiB) if no specific value is required.");
        }

        _filePath = Path.GetFullPath(filePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxFileBytes = maxFileBytes;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Absolute, normalized path to the JSON-lines audit fallback
    /// file. Exposed for diagnostics / tests so a probe can read the
    /// file back without re-deriving the path.
    /// </summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Configured on-disk size cap for <see cref="FilePath"/>, in
    /// bytes. Writes that would push the file past this threshold
    /// are refused with
    /// <see cref="AuditFallbackCapacityExceededException"/> and a
    /// <see cref="LogLevel.Critical"/> log entry. Exposed for
    /// diagnostics so a health probe can report the configured cap
    /// alongside the current file size.
    /// </summary>
    public long MaxFileBytes => _maxFileBytes;

    /// <inheritdoc />
    public async Task EnqueueAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = TypeGeneral,
            ["entryId"] = entry.EntryId,
            ["messageId"] = entry.MessageId,
            ["userId"] = entry.UserId,
            ["agentId"] = entry.AgentId,
            ["action"] = entry.Action,
            ["eventFamily"] = entry.EventFamily,
            ["timestamp"] = entry.Timestamp.ToUnixTimeMilliseconds(),
            ["correlationId"] = entry.CorrelationId,
            ["tenantId"] = entry.TenantId,
            ["details"] = entry.Details,
        };

        await WriteLineAsync(payload, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(HumanResponseAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = TypeHumanResponse,
            ["entryId"] = entry.EntryId,
            ["messageId"] = entry.MessageId,
            ["userId"] = entry.UserId,
            ["agentId"] = entry.AgentId,
            ["questionId"] = entry.QuestionId,
            ["actionValue"] = entry.ActionValue,
            ["comment"] = entry.Comment,
            ["timestamp"] = entry.Timestamp.ToUnixTimeMilliseconds(),
            ["correlationId"] = entry.CorrelationId,
            ["tenantId"] = entry.TenantId,
            ["details"] = entry.Details,
        };

        await WriteLineAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the kernel handle held by the internal
    /// <see cref="SemaphoreSlim"/>. <c>SemaphoreSlim</c> lazily
    /// allocates a <see cref="ManualResetEvent"/> (and therefore an
    /// OS handle) the first time
    /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    /// observes contention; because the sink is normally registered
    /// as a DI singleton, that handle would otherwise outlive the
    /// host on graceful shutdown. The DI container disposes the sink
    /// during shutdown which triggers this method and releases the
    /// handle. Dispose is idempotent via an
    /// <see cref="Interlocked.Exchange(ref int,int)"/> flag so
    /// double-disposal — manual <c>Dispose()</c> followed by
    /// container shutdown, or vice versa — is safe and a no-op.
    /// Hosts should not call <see cref="EnqueueAsync(AuditEntry,CancellationToken)"/>
    /// after disposing the sink; that ordering is the container's
    /// responsibility (singletons are disposed after hosted services
    /// stop in the standard .NET Generic Host).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _gate.Dispose();
    }

    private async Task WriteLineAsync(IDictionary<string, object?> payload, CancellationToken ct)
    {
        // Serialise OUTSIDE the lock so a slow serializer cannot block
        // a concurrent writer (the lock guards only the size-cap check
        // and the append).
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var line = json + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Disk-fill guard. The check MUST live inside the gate
            // because two concurrent writers each observing
            // (currentLength + bytes <= cap) before either appends
            // would otherwise race past the threshold. We do not
            // pre-compute the length once at construction time
            // because (a) the file may already exist from a previous
            // process run (durability is the whole point of this
            // sink) and (b) external operator action — replay,
            // rotate, truncate — can shrink the file at any moment
            // and we want the next write to succeed immediately
            // once the operator has freed space.
            long currentLength = 0;
            if (File.Exists(_filePath))
            {
                currentLength = new FileInfo(_filePath).Length;
            }

            if (currentLength + bytes.LongLength > _maxFileBytes)
            {
                _logger.LogCritical(
                    "FileAuditFallbackSink REFUSED to persist audit fallback row to {FilePath}: current file size {CurrentBytes} bytes plus new row of {NewBytes} bytes would exceed the configured size cap of {MaxBytes} bytes. Both the primary audit DB AND the durable file-backed fallback are now unavailable for new rows — operator MUST rotate, replay, or truncate {FilePath} before further rejection audit rows can be persisted. (Letting the file grow unbounded would fill the local disk and crash the host process, taking the primary audit path with it.)",
                    _filePath,
                    currentLength,
                    bytes.LongLength,
                    _maxFileBytes);

                throw new AuditFallbackCapacityExceededException(
                    _filePath,
                    currentLength,
                    bytes.LongLength,
                    _maxFileBytes);
            }

            // WriteThrough so the line is on disk before the call
            // returns — matches the "MUST persist before returning"
            // contract in IAuditFallbackSink.EnqueueAsync. FileShare.Read
            // so support / forensic tooling can tail the file live
            // without blocking the writer.
            await using var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException
            && ex is not AuditFallbackCapacityExceededException)
        {
            // The cap-exceeded path has already produced a precise
            // Critical log; double-logging it here as a generic
            // "failed to persist" would dilute the operator-actionable
            // signal. All other failure modes (disk full BEFORE the
            // cap, read-only directory, kernel handle refusal, …)
            // hit this branch and get the generic Critical escalation.
            _logger.LogCritical(
                ex,
                "FileAuditFallbackSink failed to persist audit fallback row to {FilePath}. Both the primary audit DB AND the durable file-backed fallback have now failed — operator intervention is required to recover the missing audit row(s).",
                _filePath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Thrown by <see cref="FileAuditFallbackSink"/> when a write would
/// push the on-disk file past its configured size cap
/// (<see cref="FileAuditFallbackSink.MaxFileBytes"/>). Derives from
/// <see cref="IOException"/> so callers that already catch I/O
/// failures from the sink (the pipeline's
/// <c>WriteRejectionAuditAsync</c> Critical-on-fallback-failure
/// branch) continue to handle the cap as a fallback-failure event,
/// while operator-side replay tooling can detect this specific
/// exception type via <c>catch (AuditFallbackCapacityExceededException)</c>
/// to distinguish "operator must rotate the fallback file"
/// (recoverable) from "the disk is full" (host-level incident) and
/// route the alert appropriately.
/// </summary>
public sealed class AuditFallbackCapacityExceededException : IOException
{
    public AuditFallbackCapacityExceededException(
        string filePath,
        long currentBytes,
        long newBytes,
        long maxBytes)
        : base(
            $"FileAuditFallbackSink refused to persist a fallback audit row: current file size {currentBytes} bytes plus new row of {newBytes} bytes would exceed the configured size cap of {maxBytes} bytes for '{filePath}'. Rotate, replay, or truncate the file before further rejection audit rows can be persisted.")
    {
        this.FilePath = filePath;
        this.CurrentBytes = currentBytes;
        this.NewBytes = newBytes;
        this.MaxBytes = maxBytes;
    }

    /// <summary>Absolute path of the fallback file that hit the cap.</summary>
    public string FilePath { get; }

    /// <summary>Observed on-disk size at the moment the write was refused.</summary>
    public long CurrentBytes { get; }

    /// <summary>Size in bytes of the row that was refused.</summary>
    public long NewBytes { get; }

    /// <summary>Configured cap from <see cref="FileAuditFallbackSink.MaxFileBytes"/>.</summary>
    public long MaxBytes { get; }
}
