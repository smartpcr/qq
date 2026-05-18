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
/// <b>Failure semantics.</b> If the fallback ALSO fails (the local
/// disk is full, the directory is read-only, the OS refuses the
/// handle, etc.) the sink rethrows so the caller can decide. The
/// pipeline's <c>WriteRejectionAuditAsync</c> catches and logs the
/// rethrown exception at <see cref="LogLevel.Critical"/>, escalating
/// the operator alert beyond the warn-level the primary-only path
/// emits — at that point both audit storage layers have failed and a
/// human MUST be paged. The denial response itself still fires
/// because the security-critical user-facing reply is the higher-
/// priority path; the deferred alert is the recovery mechanism.
/// </para>
/// </remarks>
public sealed class FileAuditFallbackSink : IAuditFallbackSink
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
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    public FileAuditFallbackSink(string filePath, ILogger<FileAuditFallbackSink> logger)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "FileAuditFallbackSink requires a non-empty file path. Hosts that genuinely tolerate audit gaps must register NullAuditFallbackSink explicitly.",
                nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

    private async Task WriteLineAsync(IDictionary<string, object?> payload, CancellationToken ct)
    {
        // Serialise OUTSIDE the lock so a slow serializer cannot block
        // a concurrent writer (the lock guards only the append).
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var line = json + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
