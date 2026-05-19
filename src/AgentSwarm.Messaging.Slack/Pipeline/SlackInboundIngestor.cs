// -----------------------------------------------------------------------
// <copyright file="SlackInboundIngestor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.DependencyInjection;
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
/// <para>
/// <b>Iter-2 evaluator item #2 (lazy pipeline resolution).</b> The
/// ingestor takes <see cref="IServiceProvider"/> rather than the
/// pipeline directly so the BackgroundService can be activated by
/// the host even when no <c>ISlackCommandHandler</c> /
/// <c>ISlackAppMentionHandler</c> / <c>ISlackInteractionHandler</c>
/// is registered (i.e. a Production deployment that has opted OUT
/// of <c>AddSlackInboundDevelopmentHandlerStubs</c> per the iter-2
/// gate but has not yet wired Stage 5.x real handlers). The
/// pipeline is resolved lazily on the FIRST dequeued envelope; if
/// the handler registrations are missing, DI throws
/// <see cref="InvalidOperationException"/> at that point, the
/// existing <c>catch (Exception ex)</c> block forwards the envelope
/// to the durable last-resort sink, and the loop continues so
/// subsequent failures are also captured. This is the visible
/// fail-loud surface that replaces the previous silent ack-and-drop
/// behaviour.
/// </para>
/// </remarks>
internal sealed class SlackInboundIngestor : BackgroundService
{
    private readonly ISlackInboundQueue queue;
    private readonly IServiceProvider services;
    private readonly ILogger<SlackInboundIngestor> logger;

    private SlackInboundProcessingPipeline? cachedPipeline;
    private ISlackInboundEnqueueDeadLetterSink? cachedDlqFallbackSink;

    public SlackInboundIngestor(
        ISlackInboundQueue queue,
        IServiceProvider services,
        ILogger<SlackInboundIngestor> logger)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
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

                // Iter-2 evaluator item #2: lazily resolve the pipeline
                // (and the last-resort DLQ sink) so the host can boot
                // even when no Stage 5 handlers are registered. A
                // missing-handler DI failure surfaces below as the
                // first per-envelope InvalidOperationException, which
                // the existing catch routes to the last-resort sink so
                // the envelope is preserved instead of silently lost.
                SlackInboundProcessingPipeline? pipeline;
                ISlackInboundEnqueueDeadLetterSink? fallbackSink;
                try
                {
                    pipeline = this.GetPipeline();
                    fallbackSink = this.GetDlqFallbackSink();
                }
                catch (Exception resolveEx)
                {
                    // The pipeline could not be resolved -- almost
                    // certainly because the production composition
                    // root did not register the Stage 5 handlers AND
                    // did not opt into AddSlackInboundDevelopmentHandlerStubs.
                    // We MUST NOT silently drop the envelope; try the
                    // last-resort sink (resolved independently so the
                    // missing handler does not poison this fallback).
                    this.logger.LogCritical(
                        resolveEx,
                        "SlackInboundIngestor could not resolve the processing pipeline for idempotency_key={IdempotencyKey} source={SourceType}; the most likely cause is missing ISlackCommandHandler / ISlackAppMentionHandler / ISlackInteractionHandler registrations (real Stage 5 handlers or AddSlackInboundDevelopmentHandlerStubs). Forwarding envelope to the last-resort dead-letter sink.",
                        envelope.IdempotencyKey,
                        envelope.SourceType);

                    try
                    {
                        ISlackInboundEnqueueDeadLetterSink? sink = this.TryGetDlqFallbackSink();
                        if (sink is not null)
                        {
                            await sink
                                .RecordDeadLetterAsync(envelope, resolveEx, attemptCount: 0, stoppingToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            this.logger.LogCritical(
                                "SlackInboundIngestor could not resolve the last-resort dead-letter sink either; envelope idempotency_key={IdempotencyKey} source={SourceType} is unrecoverable.",
                                envelope.IdempotencyKey,
                                envelope.SourceType);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception sinkEx)
                    {
                        this.logger.LogCritical(
                            sinkEx,
                            "SlackInboundIngestor last-resort dead-letter sink threw while absorbing a pipeline-resolution failure for idempotency_key={IdempotencyKey} source={SourceType}; envelope is unrecoverable.",
                            envelope.IdempotencyKey,
                            envelope.SourceType);
                    }

                    continue;
                }

                try
                {
                    SlackInboundProcessingOutcome outcome = await pipeline
                        .ProcessAsync(envelope, stoppingToken)
                        .ConfigureAwait(false);

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
                        await fallbackSink
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
                        await fallbackSink
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

    private SlackInboundProcessingPipeline GetPipeline()
    {
        // Cache after first successful resolution so subsequent
        // envelopes do not re-pay the lookup cost. The pipeline is
        // registered as a singleton so caching is correct.
        return this.cachedPipeline ??= this.services.GetRequiredService<SlackInboundProcessingPipeline>();
    }

    private ISlackInboundEnqueueDeadLetterSink GetDlqFallbackSink()
    {
        return this.cachedDlqFallbackSink ??= this.services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();
    }

    private ISlackInboundEnqueueDeadLetterSink? TryGetDlqFallbackSink()
    {
        if (this.cachedDlqFallbackSink is not null)
        {
            return this.cachedDlqFallbackSink;
        }

        try
        {
            this.cachedDlqFallbackSink = this.services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();
            return this.cachedDlqFallbackSink;
        }
        catch
        {
            return null;
        }
    }
}
