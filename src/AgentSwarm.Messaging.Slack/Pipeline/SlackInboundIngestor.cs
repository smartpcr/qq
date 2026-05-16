// -----------------------------------------------------------------------
// <copyright file="SlackInboundIngestor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Observability;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background service that drains <see cref="ISlackInboundQueue"/> and
/// delegates each envelope to <see cref="SlackInboundProcessingPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// implementation step 1. The ingestor is intentionally thin -- the
/// pipeline owns authorization / dedup / dispatch / retry / DLQ so
/// this service can be reduced to a dequeue loop with operational
/// logging.
/// </para>
/// <para>
/// Failure semantics: the pipeline already owns the retry / DLQ
/// path for handler failures, so a thrown exception from
/// <see cref="SlackInboundProcessingPipeline.ProcessAsync"/> always
/// represents an infrastructure surface (idempotency-table write,
/// audit writer, DLQ backend) that the pipeline could not absorb.
/// Because <see cref="Queues.ISlackInboundQueue"/> has no
/// nack/requeue contract, the dequeued envelope is already gone from
/// the inbound queue when the throw arrives; to honour the story's
/// FR-005 / FR-007 zero-message-loss guarantee, the ingestor forwards
/// EVERY non-cancellation pipeline exception (including the explicit
/// <see cref="SlackInboundDeadLetterEnqueueException"/> raised when
/// the primary DLQ backend itself failed) to the durable last-resort
/// <see cref="ISlackInboundEnqueueDeadLetterSink"/> before continuing
/// the loop. The only exception the ingestor still propagates is
/// <see cref="OperationCanceledException"/> on shutdown (so
/// <see cref="BackgroundService.ExecuteAsync"/> exits cleanly).
/// </para>
/// </remarks>
internal sealed class SlackInboundIngestor : BackgroundService
{
    private readonly ISlackInboundQueue queue;
    private readonly SlackInboundProcessingPipeline pipeline;
    private readonly ISlackInboundEnqueueDeadLetterSink dlqFallbackSink;
    private readonly ILogger<SlackInboundIngestor> logger;

    public SlackInboundIngestor(
        ISlackInboundQueue queue,
        SlackInboundProcessingPipeline pipeline,
        ISlackInboundEnqueueDeadLetterSink dlqFallbackSink,
        ILogger<SlackInboundIngestor> logger)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.dlqFallbackSink = dlqFallbackSink ?? throw new ArgumentNullException(nameof(dlqFallbackSink));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.logger.LogInformation("SlackInboundIngestor starting.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SlackInboundEnvelope envelope;
                try
                {
                    envelope = await this.queue
                        .DequeueAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // Stage 7.2: bump `slack.inbound.count` for every
                // envelope drained from the queue, tagged with the
                // source type so dashboards can split command /
                // interaction / event traffic. Wrap the per-envelope
                // pipeline work in a parent `slack.inbound.receive`
                // span (ActivityKind.Consumer marks the queue-drain
                // boundary per OTel semantic conventions).
                SlackTelemetry.InboundCount.Add(
                    1,
                    new KeyValuePair<string, object?>(SlackTelemetry.AttributeSourceType, envelope.SourceType.ToString()),
                    new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, envelope.TeamId ?? string.Empty));

                using Activity? receiveSpan = SlackTelemetry.StartInboundSpan(
                    SlackTelemetry.InboundReceiveSpanName,
                    envelope,
                    ActivityKind.Consumer);

                using IDisposable scope = SlackTelemetry.CreateScope(this.logger, envelope);

                try
                {
                    SlackInboundProcessingOutcome outcome = await this.pipeline
                        .ProcessAsync(envelope, stoppingToken)
                        .ConfigureAwait(false);

                    receiveSpan?.SetTag(SlackTelemetry.AttributeOutcome, outcome.ToString());

                    this.logger.LogDebug(
                        "SlackInboundIngestor processed envelope idempotency_key={IdempotencyKey} source={SourceType} outcome={Outcome}.",
                        envelope.IdempotencyKey,
                        envelope.SourceType,
                        outcome);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SlackInboundDeadLetterEnqueueException dlqEnqueueEx)
                {
                    // The pipeline retried the handler, exhausted its
                    // budget, then attempted to hand the envelope to
                    // ISlackDeadLetterQueue -- and the DLQ backend
                    // itself blew up. Since ISlackInboundQueue has no
                    // nack/requeue contract the envelope is already
                    // gone from the inbound queue, so the only way to
                    // honor the story's no-loss guarantee is to forward
                    // the envelope to the durable last-resort sink
                    // (Stage 4.1's ISlackInboundEnqueueDeadLetterSink:
                    // bounded ring buffer + LogCritical by default,
                    // upgradeable to FileSystemSlackInboundEnqueueDeadLetterSink
                    // for JSONL on disk). The sink's docstring promises
                    // it absorbs its own failures, but we still wrap
                    // defensively so a sink throw cannot kill the loop.
                    this.logger.LogCritical(
                        dlqEnqueueEx,
                        "SlackInboundIngestor DLQ enqueue failed for idempotency_key={IdempotencyKey} source={SourceType} after {AttemptCount} handler attempts; forwarding envelope to last-resort dead-letter sink to preserve at-least-once delivery semantics.",
                        envelope.IdempotencyKey,
                        envelope.SourceType,
                        dlqEnqueueEx.AttemptCount);

                    try
                    {
                        await this.dlqFallbackSink
                            .RecordDeadLetterAsync(envelope, dlqEnqueueEx, dlqEnqueueEx.AttemptCount, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception sinkEx)
                    {
                        this.logger.LogCritical(
                            sinkEx,
                            "SlackInboundIngestor last-resort dead-letter sink threw for idempotency_key={IdempotencyKey} source={SourceType}; envelope is unrecoverable.",
                            envelope.IdempotencyKey,
                            envelope.SourceType);
                    }
                }
                catch (Exception ex)
                {
                    // Iter-5 evaluator item #1: the pipeline only
                    // throws non-cancellation exceptions when an
                    // infrastructure surface (idempotency-table
                    // probe/SaveChanges, audit writer, DLQ backend)
                    // failed in a way the pipeline itself could not
                    // absorb -- e.g.
                    // SlackIdempotencyGuard.TryAcquireAsync propagates
                    // a transient DbUpdateException that has no
                    // competing row, rather than silently dropping
                    // the envelope as a duplicate. Since
                    // ISlackInboundQueue has no nack/requeue contract
                    // the envelope is already gone from the inbound
                    // queue, so just logging and continuing would
                    // permanently lose the payload (violates the
                    // story's FR-005 / FR-007 zero-loss expectation).
                    // Forward the envelope to the durable last-resort
                    // sink (bounded ring buffer + LogCritical by
                    // default, upgradeable to JSONL on disk) so an
                    // operator can replay it after the upstream
                    // surface recovers. Wrap the forward in its own
                    // try/catch so a sink throw cannot kill the loop.
                    this.logger.LogError(
                        ex,
                        "SlackInboundIngestor pipeline threw unexpectedly for idempotency_key={IdempotencyKey} source={SourceType}; forwarding envelope to last-resort dead-letter sink to preserve at-least-once delivery semantics.",
                        envelope.IdempotencyKey,
                        envelope.SourceType);

                    try
                    {
                        await this.dlqFallbackSink
                            .RecordDeadLetterAsync(envelope, ex, attemptCount: 0, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception sinkEx)
                    {
                        this.logger.LogCritical(
                            sinkEx,
                            "SlackInboundIngestor last-resort dead-letter sink threw while absorbing an unexpected pipeline exception for idempotency_key={IdempotencyKey} source={SourceType}; envelope is unrecoverable.",
                            envelope.IdempotencyKey,
                            envelope.SourceType);
                    }
                }
            }
        }
        finally
        {
            this.logger.LogInformation("SlackInboundIngestor stopping.");
        }
    }
}
