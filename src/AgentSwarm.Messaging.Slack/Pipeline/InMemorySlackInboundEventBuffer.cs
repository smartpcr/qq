// -----------------------------------------------------------------------
// <copyright file="InMemorySlackInboundEventBuffer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// In-process bounded FIFO implementation of
/// <see cref="ISlackInboundEventBuffer"/> backed by a
/// <see cref="ConcurrentQueue{T}"/>. Producers (the
/// <see cref="BufferingAgentTaskServiceDecorator"/> registered by
/// <c>AddSlackMessenger</c>) enqueue events lock-free; consumers
/// (<see cref="SlackConnector.ReceiveAsync"/>) drain them
/// non-blockingly in batches.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.1 -- the buffer's authoritative copy of every event lives
/// in the durable audit table (<c>slack_audit_entry</c>), so a process
/// restart that drops the in-memory buffer is acceptable. Hosts that
/// need cross-restart durability for the polling consumer can register
/// a custom implementation BEFORE
/// <c>AddSlackMessenger</c> and the TryAdd-style binding in the
/// facade defers to it.
/// </para>
/// <para>
/// Capacity is configurable via
/// <see cref="SlackInboundEventBufferOptions.Capacity"/> (default 1024
/// events) and the overflow behaviour via
/// <see cref="SlackInboundEventBufferOptions.OverflowPolicy"/>
/// (default <see cref="SlackInboundEventBufferOverflowPolicy.DropNewest"/>
/// after Stage 8.1 iter-2). With drop-newest, the oldest never-drained
/// events are preserved so a stalled poller eventually surfaces them
/// once it catches up; with drop-oldest (legacy), the buffer behaves
/// as a ring and the OLDEST event is evicted first. Either policy
/// increments <see cref="Dropped"/> so a future health-check probe
/// can alarm on sustained back-pressure, and the dropped event
/// remains queryable through the audit log so no data is lost from
/// the audit perspective.
/// </para>
/// </remarks>
internal sealed class InMemorySlackInboundEventBuffer : ISlackInboundEventBuffer
{
    /// <summary>
    /// Fallback capacity used when no
    /// <see cref="SlackInboundEventBufferOptions"/> are bound or the
    /// configured value is non-positive.
    /// </summary>
    internal const int DefaultCapacity = 1024;

    private readonly int capacity;
    private readonly SlackInboundEventBufferOverflowPolicy overflowPolicy;
    private readonly ConcurrentQueue<MessengerEvent> queue = new();
    private readonly object enqueueLock = new();
    private long dropped;

    public InMemorySlackInboundEventBuffer()
        : this(options: null)
    {
    }

    public InMemorySlackInboundEventBuffer(IOptions<SlackInboundEventBufferOptions>? options)
    {
        SlackInboundEventBufferOptions? bound = options?.Value;
        int configured = bound?.Capacity ?? DefaultCapacity;
        this.capacity = configured > 0 ? configured : DefaultCapacity;
        this.overflowPolicy = bound?.OverflowPolicy
            ?? SlackInboundEventBufferOverflowPolicy.DropNewest;
    }

    /// <inheritdoc />
    public int Count => this.queue.Count;

    /// <inheritdoc />
    public long Dropped => Interlocked.Read(ref this.dropped);

    /// <inheritdoc />
    public void Enqueue(MessengerEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        // The drop decision MUST observe a coherent count snapshot so
        // two concurrent producers cannot both treat the buffer as
        // having room and overshoot capacity by two. A lock around
        // the count-check + enqueue/drop is the simplest way to keep
        // the policy precise without re-implementing a Channel.
        lock (this.enqueueLock)
        {
            if (this.queue.Count >= this.capacity)
            {
                switch (this.overflowPolicy)
                {
                    case SlackInboundEventBufferOverflowPolicy.DropNewest:
                    case SlackInboundEventBufferOverflowPolicy.Reject:
                        // Reject the incoming event so the oldest
                        // never-drained events survive. The caller
                        // (BufferingAgentTaskServiceDecorator)
                        // swallows the silent drop; the audit log
                        // remains the authoritative copy.
                        Interlocked.Increment(ref this.dropped);
                        return;

                    case SlackInboundEventBufferOverflowPolicy.DropOldest:
                        // Legacy ring-buffer semantics: evict the
                        // head to make room for the newcomer. Hosts
                        // that explicitly prefer freshness over
                        // first-in / first-out opt into this.
                        while (this.queue.Count >= this.capacity)
                        {
                            if (this.queue.TryDequeue(out _))
                            {
                                Interlocked.Increment(ref this.dropped);
                            }
                            else
                            {
                                break;
                            }
                        }

                        break;
                }
            }

            this.queue.Enqueue(ev);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MessengerEvent> Drain(int maxItems)
    {
        if (maxItems <= 0)
        {
            return Array.Empty<MessengerEvent>();
        }

        List<MessengerEvent>? drained = null;
        for (int i = 0; i < maxItems; i++)
        {
            if (!this.queue.TryDequeue(out MessengerEvent? ev))
            {
                break;
            }

            drained ??= new List<MessengerEvent>(Math.Min(maxItems, 16));
            drained.Add(ev);
        }

        return (IReadOnlyList<MessengerEvent>?)drained ?? Array.Empty<MessengerEvent>();
    }
}
