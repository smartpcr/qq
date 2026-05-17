// -----------------------------------------------------------------------
// <copyright file="ISlackInboundEnqueueDeadLetterSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sink invoked by <see cref="SlackInboundEnqueueScheduler"/> when every
/// retry of <see cref="Queues.ISlackInboundQueue.EnqueueAsync"/> for a
/// given <see cref="SlackInboundEnvelope"/> has failed AFTER the Slack
/// ACK has already been written. The contract exists so a permanently
/// rejected envelope is recoverable / observable rather than silently
/// lost via a log line (Stage 4.1 iter-3 evaluator item 2).
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 ships <see cref="InMemorySlackInboundEnqueueDeadLetterSink"/>
/// as the default registration. Operators running on top of a durable
/// queue (Service Bus, EventHub, EF Core inbox table) register their own
/// implementation BEFORE calling
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport"/>
/// to forward dead-letter envelopes to a durable sink.
/// </para>
/// <para>
/// Implementations MUST be thread-safe -- the scheduler invokes the sink
/// from a <see cref="Microsoft.AspNetCore.Http.HttpResponse.OnCompleted(System.Func{System.Threading.Tasks.Task})"/>
/// callback that runs after the HTTP response has been flushed, so
/// concurrent calls are expected under load. Implementations SHOULD
/// also be non-blocking; the callback runs on a thread-pool thread and
/// blocking work delays the next request that lands on the same thread.
/// </para>
/// </remarks>
internal interface ISlackInboundEnqueueDeadLetterSink
{
    /// <summary>
    /// Records the envelope as un-enqueueable. Implementations decide
    /// what "record" means -- forwarding to a durable inbox table,
    /// writing a JSON file under the diagnostics dir, raising a
    /// distributed tracing event, etc. The scheduler swallows any
    /// exception this method raises (it has already swallowed the
    /// inner queue failure to keep the ACK clean), so implementations
    /// SHOULD route failures through their own observability channel.
    /// </summary>
    /// <param name="envelope">The envelope that could not be enqueued
    /// despite the scheduler's retries.</param>
    /// <param name="lastException">The last exception raised by
    /// <see cref="Queues.ISlackInboundQueue.EnqueueAsync"/>.</param>
    /// <param name="attemptCount">Number of total attempts the
    /// scheduler made before giving up (matches
    /// <see cref="SlackInboundEnqueueScheduler.MaxAttempts"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordDeadLetterAsync(
        SlackInboundEnvelope envelope,
        Exception lastException,
        int attemptCount,
        CancellationToken ct);
}
