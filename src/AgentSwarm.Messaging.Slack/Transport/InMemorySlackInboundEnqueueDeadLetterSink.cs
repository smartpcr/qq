// -----------------------------------------------------------------------
// <copyright file="InMemorySlackInboundEnqueueDeadLetterSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackInboundEnqueueDeadLetterSink"/> for Stage
/// 4.1. Captures dead-lettered envelopes into a bounded in-memory ring
/// buffer (max <see cref="DefaultCapacity"/> entries) AND emits a
/// <c>LogCritical</c> entry per failure. Both surfaces are observable
/// (the log goes to the host's standard sink; the ring buffer is
/// queryable via <see cref="DrainCaptured"/> for diagnostics endpoints
/// and unit tests).
/// </summary>
/// <remarks>
/// <para>
/// In-process is the right default for Stage 4.1: the brief does not
/// own the durable inbox table (Stage 4.3 owns
/// <c>slack_inbound_request_record</c>), but post-ACK enqueue failures
/// MUST NOT vanish silently. An operator running with this default
/// sink and seeing the LogCritical can either (a) replay the captured
/// envelopes manually after fixing the queue or (b) swap in a durable
/// sink registration to forward future failures elsewhere.
/// </para>
/// <para>
/// The ring buffer evicts the oldest entry once
/// <see cref="DefaultCapacity"/> is hit so a sustained queue outage
/// cannot grow the process memory unbounded.
/// </para>
/// </remarks>
internal sealed class InMemorySlackInboundEnqueueDeadLetterSink : ISlackInboundEnqueueDeadLetterSink
{
    /// <summary>
    /// Maximum number of dead-lettered envelopes the sink retains
    /// in-memory before evicting the oldest. 512 is generous for a
    /// transient queue outage and bounded enough to stay well under
    /// any reasonable process memory budget.
    /// </summary>
    public const int DefaultCapacity = 512;

    private readonly ILogger<InMemorySlackInboundEnqueueDeadLetterSink> logger;
    private readonly ConcurrentQueue<SlackInboundDeadLetterEntry> entries = new();
    private readonly int capacity;
    private long deadLetterCount;

    public InMemorySlackInboundEnqueueDeadLetterSink(
        ILogger<InMemorySlackInboundEnqueueDeadLetterSink> logger)
        : this(logger, DefaultCapacity)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a unit test pick a
    /// smaller capacity to validate the eviction path.
    /// </summary>
    public InMemorySlackInboundEnqueueDeadLetterSink(
        ILogger<InMemorySlackInboundEnqueueDeadLetterSink> logger,
        int capacity)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    /// <summary>
    /// Total number of dead-letter recordings observed since process
    /// start. Monotonically increasing -- not decremented by drains.
    /// Exposed for the metrics pipeline (Stage 7.x supplies the typed
    /// <c>Meter</c> wrapper).
    /// </summary>
    public long DeadLetterCount => Interlocked.Read(ref this.deadLetterCount);

    /// <inheritdoc />
    public Task RecordDeadLetterAsync(
        SlackInboundEnvelope envelope,
        Exception lastException,
        int attemptCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lastException);

        Interlocked.Increment(ref this.deadLetterCount);

        SlackInboundDeadLetterEntry entry = new(
            envelope,
            lastException.GetType().FullName ?? "UnknownException",
            lastException.Message,
            attemptCount,
            DateTimeOffset.UtcNow);

        this.entries.Enqueue(entry);

        // Evict oldest entries until we are within capacity. The drain
        // races with concurrent writers; using a Count snapshot keeps
        // the loop bounded.
        while (this.entries.Count > this.capacity && this.entries.TryDequeue(out _))
        {
            // Intentionally empty -- the dequeue itself is the eviction.
        }

        this.logger.LogCritical(
            lastException,
            "Slack inbound envelope DEAD-LETTERED after {AttemptCount} attempts: idempotency_key={IdempotencyKey} source={SourceType} team_id={TeamId} channel_id={ChannelId}. The Slack ACK was already sent; this envelope will not be replayed automatically. {EnvelopesPendingDrain} envelope(s) await operator drain via InMemorySlackInboundEnqueueDeadLetterSink.DrainCaptured().",
            attemptCount,
            envelope.IdempotencyKey,
            envelope.SourceType,
            envelope.TeamId,
            envelope.ChannelId,
            this.entries.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Atomically returns the captured entries and clears the ring
    /// buffer. Intended for operator-driven drain (admin endpoint,
    /// diagnostics command) and for the iter-3 unit test that pins
    /// the dead-letter contract.
    /// </summary>
    public IReadOnlyList<SlackInboundDeadLetterEntry> DrainCaptured()
    {
        List<SlackInboundDeadLetterEntry> drained = new();
        while (this.entries.TryDequeue(out SlackInboundDeadLetterEntry? entry))
        {
            drained.Add(entry);
        }

        return drained;
    }

    /// <summary>
    /// Returns a snapshot of the captured entries WITHOUT clearing
    /// them. Useful for diagnostics that want to inspect the
    /// dead-letter buffer without consuming it.
    /// </summary>
    public IReadOnlyList<SlackInboundDeadLetterEntry> PeekCaptured()
    {
        return new List<SlackInboundDeadLetterEntry>(this.entries);
    }
}

/// <summary>
/// Captures a single dead-letter recording made by
/// <see cref="InMemorySlackInboundEnqueueDeadLetterSink"/>. Kept as a
/// flat record (no nested exception) so the entry is safely
/// serializable for diagnostics dumps.
/// </summary>
internal sealed record SlackInboundDeadLetterEntry(
    SlackInboundEnvelope Envelope,
    string ExceptionType,
    string ExceptionMessage,
    int AttemptCount,
    DateTimeOffset RecordedAt);
