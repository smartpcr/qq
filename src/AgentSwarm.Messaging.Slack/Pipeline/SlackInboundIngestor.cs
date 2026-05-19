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
/// Failure semantics: <see cref="Queues.ISlackInboundQueue"/> has no
/// nack/requeue contract, so a dequeued envelope is already gone from
/// the inbound queue when a downstream throw arrives. To honour the
/// story's FR-005 / FR-007 zero-message-loss guarantee, every
/// non-cancellation failure -- pipeline lazy-resolution, the explicit
/// <see cref="SlackInboundDeadLetterEnqueueException"/> raised when
/// the primary DLQ backend itself failed, and any other pipeline
/// exception -- is forwarded to the durable last-resort
/// <see cref="ISlackInboundEnqueueDeadLetterSink"/> before the loop
/// continues. <see cref="OperationCanceledException"/> on shutdown is
/// the only exception still propagated, so
/// <see cref="BackgroundService.ExecuteAsync"/> exits cleanly.
/// </para>
/// <para>
/// The pipeline is resolved lazily via <see cref="IServiceProvider"/>
/// (not ctor-injected) so the BackgroundService activates even when
/// no <c>ISlackCommandHandler</c> / <c>ISlackAppMentionHandler</c> /
/// <c>ISlackInteractionHandler</c> is registered. A missing-handler
/// DI failure then surfaces as the FIRST per-envelope
/// <see cref="InvalidOperationException"/> from the pipeline ctor and
/// is captured by the resolve-failure catch below.
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

                // Lazily resolve the pipeline (and last-resort DLQ
                // sink) so the host can boot even when no Stage 5
                // handlers are registered. A missing-handler DI
                // failure surfaces as an InvalidOperationException
                // from the pipeline ctor; the catch routes the
                // envelope to the last-resort sink so it is preserved
                // instead of silently lost.
                SlackInboundProcessingPipeline? pipeline;
                ISlackInboundEnqueueDeadLetterSink? fallbackSink;
                try
                {
                    pipeline = this.GetPipeline();
                    fallbackSink = this.GetDlqFallbackSink();
                }
                catch (Exception resolveEx)
                {
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
                    // The pipeline exhausted its retry budget and
                    // then the primary DLQ backend itself failed.
                    // ISlackInboundQueue has no nack contract, so
                    // forward to the durable last-resort sink to
                    // preserve at-least-once delivery semantics.
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
                    // The pipeline propagates non-cancellation
                    // exceptions only when an infrastructure surface
                    // (idempotency-table probe/SaveChanges, audit
                    // writer, DLQ backend) failed in a way the
                    // pipeline itself could not absorb. Forward to
                    // the last-resort sink so the envelope is not
                    // permanently lost -- ISlackInboundQueue has no
                    // nack/requeue contract.
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
        // GetService<T>() returns null when the contract is not
        // registered -- preferred over a broad try/catch around
        // GetRequiredService<T>() because the null surface is
        // explicit and cannot mask an unrelated activation failure.
        return this.cachedDlqFallbackSink
            ??= this.services.GetService<ISlackInboundEnqueueDeadLetterSink>();
    }
}
