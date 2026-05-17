// -----------------------------------------------------------------------
// <copyright file="FileSystemSlackInboundEnqueueDeadLetterSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable disk-backed <see cref="ISlackInboundEnqueueDeadLetterSink"/>
/// that writes each dead-lettered envelope to a single newline-delimited
/// JSON (JSONL) file under a configurable directory. Hosts opt in by
/// calling
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddFileSystemSlackInboundEnqueueDeadLetterSink"/>
/// in their composition root.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 iter-3 introduced
/// <see cref="InMemorySlackInboundEnqueueDeadLetterSink"/> as the default
/// sink so that post-ACK enqueue failures are never silently lost in
/// production. The in-memory ring buffer is sufficient for short
/// queue outages, but its contents do NOT survive a process restart --
/// an operator who reboots the worker after a queue outage loses the
/// captured envelopes. Iter-4 ships this filesystem sink so the
/// "recoverable" surface required by the evaluator is durable across
/// restarts: each dead-letter record is a JSON line on disk that can be
/// replayed by a tool reading the file.
/// </para>
/// <para>
/// Format: one JSON object per line. Each object carries the envelope
/// fields, the last exception's type/message/stack trace, the attempt
/// count, and a UTC timestamp. Multi-line JSON formatting is
/// deliberately avoided so the file is grep-friendly and trivially
/// streamable by tail-followers.
/// </para>
/// <para>
/// Writes are append-only and synchronized through a per-instance
/// <see cref="SemaphoreSlim"/>. IO failures inside
/// <see cref="RecordDeadLetterAsync"/> are caught and logged
/// <c>Critical</c> rather than rethrown -- the caller has already lost
/// the envelope (the queue retry budget is exhausted AND the ACK has
/// already shipped) so the only useful response to a sink failure is
/// to surface the loss for the operator.
/// </para>
/// </remarks>
internal sealed class FileSystemSlackInboundEnqueueDeadLetterSink
    : ISlackInboundEnqueueDeadLetterSink, IDisposable
{
    /// <summary>
    /// Default name of the JSONL file written under the configured
    /// directory. Hosts that need rotation point this at a managed dir
    /// or supply their own filename via <see cref="AbsoluteFilePath"/>.
    /// </summary>
    public const string DefaultFileName = "slack-inbound-dead-letter.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ILogger<FileSystemSlackInboundEnqueueDeadLetterSink> logger;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool disposed;

    public FileSystemSlackInboundEnqueueDeadLetterSink(
        string directoryPath,
        ILogger<FileSystemSlackInboundEnqueueDeadLetterSink> logger)
        : this(directoryPath, DefaultFileName, logger)
    {
    }

    public FileSystemSlackInboundEnqueueDeadLetterSink(
        string directoryPath,
        string fileName,
        ILogger<FileSystemSlackInboundEnqueueDeadLetterSink> logger)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Dead-letter directory path must be supplied.", nameof(directoryPath));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Dead-letter file name must be supplied.", nameof(fileName));
        }

        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.AbsoluteDirectoryPath = Path.GetFullPath(directoryPath);
        this.AbsoluteFilePath = Path.Combine(this.AbsoluteDirectoryPath, fileName);

        // Create the directory eagerly so a missing parent dir surfaces
        // at startup (where the operator can fix it) rather than at the
        // moment of a real dead-letter (where there is no recovery).
        Directory.CreateDirectory(this.AbsoluteDirectoryPath);
    }

    /// <summary>
    /// Absolute path to the directory the sink writes into.
    /// </summary>
    public string AbsoluteDirectoryPath { get; }

    /// <summary>
    /// Absolute path to the JSONL file the sink appends to.
    /// </summary>
    public string AbsoluteFilePath { get; }

    /// <inheritdoc />
    public async Task RecordDeadLetterAsync(
        SlackInboundEnvelope envelope,
        Exception lastException,
        int attemptCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lastException);

        if (this.disposed)
        {
            this.logger.LogCritical(
                "FileSystemSlackInboundEnqueueDeadLetterSink received a dead-letter for idempotency_key={IdempotencyKey} AFTER disposal; the envelope is now LOST.",
                envelope.IdempotencyKey);
            return;
        }

        FileSystemDeadLetterRecord record = new(
            RecordedAt: DateTimeOffset.UtcNow,
            IdempotencyKey: envelope.IdempotencyKey,
            SourceType: envelope.SourceType.ToString(),
            TeamId: envelope.TeamId,
            ChannelId: envelope.ChannelId,
            UserId: envelope.UserId,
            TriggerId: envelope.TriggerId,
            RawPayload: envelope.RawPayload,
            AttemptCount: attemptCount,
            ExceptionType: lastException.GetType().FullName ?? "UnknownException",
            ExceptionMessage: lastException.Message,
            ExceptionStackTrace: lastException.StackTrace);

        string line = JsonSerializer.Serialize(record, JsonOptions);

        await this.writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // FileShare.Read so an operator can tail the file while it
            // is being written. FileMode.Append creates the file lazily
            // on first write.
            await using FileStream stream = new(
                this.AbsoluteFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            this.logger.LogCritical(
                lastException,
                "Slack inbound envelope DEAD-LETTERED to {DeadLetterFile} after {AttemptCount} attempts: idempotency_key={IdempotencyKey} source={SourceType} team_id={TeamId}.",
                this.AbsoluteFilePath,
                attemptCount,
                envelope.IdempotencyKey,
                envelope.SourceType,
                envelope.TeamId);
        }
        catch (Exception ioEx) when (ioEx is not OperationCanceledException)
        {
            // The sink itself failed. Re-raise the loss through the
            // logger at Critical so an operator alerting on log levels
            // still sees it.
            this.logger.LogCritical(
                ioEx,
                "FileSystemSlackInboundEnqueueDeadLetterSink FAILED to persist dead-letter for idempotency_key={IdempotencyKey} at {DeadLetterFile} (last enqueue exception: {LastException}). The envelope is now LOST. Verify directory permissions on {DeadLetterDirectory}.",
                envelope.IdempotencyKey,
                this.AbsoluteFilePath,
                lastException.Message,
                this.AbsoluteDirectoryPath);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    /// <summary>
    /// Reads every captured record from <see cref="AbsoluteFilePath"/>
    /// without modifying the file. Exposed for diagnostic endpoints
    /// and unit tests; an operator-driven replay tool would call this
    /// after the upstream queue is healthy again and re-enqueue each
    /// record.
    /// </summary>
    public async Task<FileSystemDeadLetterRecord[]> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(this.AbsoluteFilePath))
        {
            return Array.Empty<FileSystemDeadLetterRecord>();
        }

        await this.writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string[] lines = await File.ReadAllLinesAsync(this.AbsoluteFilePath, Encoding.UTF8, ct)
                .ConfigureAwait(false);

            FileSystemDeadLetterRecord[] records = new FileSystemDeadLetterRecord[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                records[i] = JsonSerializer.Deserialize<FileSystemDeadLetterRecord>(lines[i], JsonOptions)
                    ?? throw new InvalidOperationException(
                        string.Format(CultureInfo.InvariantCulture, "Dead-letter line {0} deserialized to null.", i));
            }

            return records;
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
        this.writeGate.Dispose();
    }
}

/// <summary>
/// Flat JSON-serializable record persisted by
/// <see cref="FileSystemSlackInboundEnqueueDeadLetterSink"/>. Carries
/// the full envelope plus the failure context so an operator-driven
/// replay tool can reconstruct the original inbound payload without
/// needing to query any other system.
/// </summary>
internal sealed record FileSystemDeadLetterRecord(
    DateTimeOffset RecordedAt,
    string IdempotencyKey,
    string SourceType,
    string TeamId,
    string? ChannelId,
    string UserId,
    string? TriggerId,
    string RawPayload,
    int AttemptCount,
    string ExceptionType,
    string ExceptionMessage,
    string? ExceptionStackTrace);
