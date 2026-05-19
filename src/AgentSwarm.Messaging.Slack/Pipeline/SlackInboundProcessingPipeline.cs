// -----------------------------------------------------------------------
// <copyright file="SlackInboundProcessingPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pure, unit-testable processor for a single
/// <see cref="SlackInboundEnvelope"/>. Owns the architecture.md §5.4
/// pipeline order: authorization -&gt; idempotency -&gt; dispatch
/// (with retry / DLQ). Decoupled from
/// <see cref="SlackInboundIngestor"/> so tests can drive a single
/// envelope through the pipeline without standing up a hosted service.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The pipeline runs every step inline on the calling task; the
/// ingestor's <see cref="System.Threading.Tasks.TaskFactory"/>
/// configuration (or absence thereof) decides whether multiple
/// envelopes interleave.
/// </para>
/// <para>
/// Dispatch rules (implementation step 5):
/// <list type="bullet">
///   <item>
///     <see cref="SlackInboundSourceType.Command"/> -&gt;
///     <see cref="ISlackCommandHandler"/>.
///   </item>
///   <item>
///     <see cref="SlackInboundSourceType.Event"/> with subtype
///     <c>app_mention</c> -&gt; <see cref="ISlackAppMentionHandler"/>.
///     Other event subtypes are logged and acknowledged with
///     <see cref="SlackInboundProcessingOutcome.Processed"/> (the
///     ingestor still owns the dedup row so a Slack retry will not
///     re-dispatch).
///   </item>
///   <item>
///     <see cref="SlackInboundSourceType.Interaction"/> -&gt;
///     <see cref="ISlackInteractionHandler"/>.
///   </item>
/// </list>
/// </para>
/// <para>
/// Retry / DLQ policy (implementation step 6): the dispatch loop runs
/// the handler up to <c>SlackRetryOptions.MaxAttempts</c> times; after
/// the final failure the envelope is forwarded to
/// <see cref="ISlackDeadLetterQueue"/> with
/// <see cref="SlackDeadLetterSource.Inbound"/>, the dedup row is
/// stamped <see cref="SlackInboundRequestProcessingStatus.Failed"/>,
/// and an <c>outcome = error</c> audit row is written.
/// </para>
/// </remarks>
internal sealed class SlackInboundProcessingPipeline
{
    private readonly ISlackInboundAuthorizer authorizer;
    private readonly ISlackIdempotencyGuard guard;
    private readonly ISlackCommandHandler commandHandler;
    private readonly ISlackAppMentionHandler appMentionHandler;
    private readonly ISlackInteractionHandler interactionHandler;
    private readonly ISlackRetryPolicy retryPolicy;
    private readonly ISlackDeadLetterQueue deadLetterQueue;
    private readonly SlackInboundAuditRecorder auditRecorder;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackInboundProcessingPipeline> logger;

    public SlackInboundProcessingPipeline(
        ISlackInboundAuthorizer authorizer,
        ISlackIdempotencyGuard guard,
        ISlackCommandHandler commandHandler,
        ISlackAppMentionHandler appMentionHandler,
        ISlackInteractionHandler interactionHandler,
        ISlackRetryPolicy retryPolicy,
        ISlackDeadLetterQueue deadLetterQueue,
        SlackInboundAuditRecorder auditRecorder,
        ILogger<SlackInboundProcessingPipeline> logger,
        TimeProvider? timeProvider = null)
    {
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
        this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        this.appMentionHandler = appMentionHandler ?? throw new ArgumentNullException(nameof(appMentionHandler));
        this.interactionHandler = interactionHandler ?? throw new ArgumentNullException(nameof(interactionHandler));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.deadLetterQueue = deadLetterQueue ?? throw new ArgumentNullException(nameof(deadLetterQueue));
        this.auditRecorder = auditRecorder ?? throw new ArgumentNullException(nameof(auditRecorder));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the full inbound pipeline for a single envelope.
    /// </summary>
    public async Task<SlackInboundProcessingOutcome> ProcessAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        // Step 1: authorize. Per architecture.md §574-575 this runs
        // BEFORE any idempotency-table write so a rejected request
        // cannot consume a dedup slot.
        SlackInboundAuthorizationResult authResult = await this.authorizer
            .AuthorizeAsync(envelope, ct)
            .ConfigureAwait(false);

        if (!authResult.IsAuthorized)
        {
            // The authorizer already wrote a rejected_auth audit row
            // through the shared ISlackAuthorizationAuditSink, so the
            // pipeline does not double-stamp here.
            this.logger.LogWarning(
                "Slack inbound pipeline rejected envelope idempotency_key={IdempotencyKey} source={SourceType} reason={Reason}.",
                envelope.IdempotencyKey,
                envelope.SourceType,
                authResult.Reason);
            return SlackInboundProcessingOutcome.Unauthorized;
        }

        // Step 2: idempotency check. The guard applies the
        // architecture.md §2.6 + §4.4 lease semantics: terminal /
        // fast-path rows AND recent 'processing' rows both return
        // false (true duplicate vs. deferred live lease); a stale
        // 'processing' row is reclaimed and returns true so a
        // crashed worker's envelope can re-dispatch. Both false
        // cases share the 'outcome = duplicate' audit marker below
        // but operators can disambiguate via the persisted row's
        // status (terminal => true duplicate, processing => deferred).
        bool acquired = await this.guard.TryAcquireAsync(envelope, ct).ConfigureAwait(false);
        string requestType = SlackInboundAuditRecorder.DescribeRequestType(envelope, this.TryReadEventSubtype(envelope));

        if (!acquired)
        {
            this.logger.LogInformation(
                "Slack inbound pipeline dropped duplicate envelope idempotency_key={IdempotencyKey} source={SourceType} team_id={TeamId} channel_id={ChannelId}.",
                envelope.IdempotencyKey,
                envelope.SourceType,
                envelope.TeamId,
                envelope.ChannelId);
            await this.auditRecorder.RecordDuplicateAsync(envelope, requestType, ct).ConfigureAwait(false);
            return SlackInboundProcessingOutcome.Duplicate;
        }

        // Step 3: dispatch with retry budget.
        DateTimeOffset firstFailedAt = default;
        Exception? lastException = null;
        SlackRetryDispatchResult dispatchResult;

        int attempt = 0;
        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            try
            {
                dispatchResult = await this.DispatchAsync(envelope, ct).ConfigureAwait(false);
                if (dispatchResult.IsSuccess)
                {
                    break;
                }

                // Non-dispatching outcomes (e.g. unrecognised event
                // subtype) are treated as success: the row stays
                // claimed so retries are deduped, but no handler ran.
                if (dispatchResult.IsAcknowledged)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (firstFailedAt == default)
                {
                    firstFailedAt = this.timeProvider.GetUtcNow();
                }

                bool retry = this.retryPolicy.ShouldRetry(attempt, ex);
                this.logger.LogWarning(
                    ex,
                    "Slack inbound handler failed: idempotency_key={IdempotencyKey} attempt={Attempt} retry={Retry} exception={ExceptionType}.",
                    envelope.IdempotencyKey,
                    attempt,
                    retry,
                    ex.GetType().FullName);

                if (!retry)
                {
                    dispatchResult = SlackRetryDispatchResult.Failure;
                    break;
                }

                TimeSpan delay = this.retryPolicy.GetDelay(attempt);
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }
        }

        if (dispatchResult.IsSuccess || dispatchResult.IsAcknowledged)
        {
            await this.guard.MarkCompletedAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
            await this.auditRecorder.RecordSuccessAsync(envelope, requestType, ct).ConfigureAwait(false);
            return SlackInboundProcessingOutcome.Processed;
        }

        // Retry budget exhausted -> DLQ + mark failed + audit error.
        DateTimeOffset deadLetteredAt = this.timeProvider.GetUtcNow();
        SlackDeadLetterEntry dlqEntry = new()
        {
            EntryId = Guid.NewGuid(),
            Source = SlackDeadLetterSource.Inbound,
            Reason = lastException?.Message ?? "inbound handler exhausted retry budget without exception.",
            ExceptionType = lastException?.GetType().FullName,
            AttemptCount = attempt,
            FirstFailedAt = firstFailedAt == default ? deadLetteredAt : firstFailedAt,
            DeadLetteredAt = deadLetteredAt,
            CorrelationId = string.IsNullOrEmpty(envelope.IdempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : envelope.IdempotencyKey,
            Payload = envelope,
        };

        try
        {
            await this.deadLetterQueue.EnqueueAsync(dlqEntry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The brief mandates that exhausted envelopes are moved
            // to the DLQ. Silently swallowing a DLQ enqueue failure
            // would permanently lose the poison message when the DLQ
            // backend is temporarily unavailable. Surface the failure
            // so the ingestor's outer log catches it AND the dedup row
            // stays in 'processing' state so operators can identify
            // stuck envelopes for manual recovery. We deliberately do
            // NOT call MarkFailedAsync here because that would falsely
            // claim the envelope reached terminal disposition.
            this.logger.LogError(
                ex,
                "Slack DLQ enqueue failed for envelope idempotency_key={IdempotencyKey} attempts={AttemptCount}; leaving dedup row in 'processing' for operator recovery and propagating.",
                envelope.IdempotencyKey,
                attempt);
            throw new SlackInboundDeadLetterEnqueueException(
                envelope.IdempotencyKey,
                attempt,
                ex);
        }

        await this.guard.MarkFailedAsync(envelope.IdempotencyKey, ct).ConfigureAwait(false);
        await this.auditRecorder
            .RecordErrorAsync(
                envelope,
                requestType,
                lastException?.Message ?? "inbound handler exhausted retry budget.",
                ct)
            .ConfigureAwait(false);

        return SlackInboundProcessingOutcome.DeadLettered;
    }

    private async Task<SlackRetryDispatchResult> DispatchAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.SourceType)
        {
            case SlackInboundSourceType.Command:
                await this.commandHandler.HandleAsync(envelope, ct).ConfigureAwait(false);
                return SlackRetryDispatchResult.Success;

            case SlackInboundSourceType.Interaction:
                await this.interactionHandler.HandleAsync(envelope, ct).ConfigureAwait(false);
                return SlackRetryDispatchResult.Success;

            case SlackInboundSourceType.Event:
                string? subtype = this.TryReadEventSubtype(envelope);
                if (string.Equals(subtype, "app_mention", StringComparison.Ordinal))
                {
                    await this.appMentionHandler.HandleAsync(envelope, ct).ConfigureAwait(false);
                    return SlackRetryDispatchResult.Success;
                }

                // Stage 4.3 only dispatches app_mention events; other
                // Events API subtypes are claimed by the dedup row
                // but acknowledged without invocation so Stage 5+ can
                // add new handlers without breaking the ingestor.
                this.logger.LogInformation(
                    "Slack inbound pipeline acknowledged event without dispatch: idempotency_key={IdempotencyKey} event_subtype={EventSubtype}.",
                    envelope.IdempotencyKey,
                    subtype ?? "(none)");
                return SlackRetryDispatchResult.Acknowledged;

            case SlackInboundSourceType.Unspecified:
            default:
                this.logger.LogWarning(
                    "Slack inbound pipeline acknowledged envelope with unknown source: idempotency_key={IdempotencyKey} source={SourceType}.",
                    envelope.IdempotencyKey,
                    envelope.SourceType);
                return SlackRetryDispatchResult.Acknowledged;
        }
    }

    private string? TryReadEventSubtype(SlackInboundEnvelope envelope)
    {
        if (envelope.SourceType != SlackInboundSourceType.Event)
        {
            return null;
        }

        if (string.IsNullOrEmpty(envelope.RawPayload))
        {
            return null;
        }

        try
        {
            SlackEventPayload parsed = SlackInboundPayloadParser.ParseEvent(envelope.RawPayload);
            return parsed.EventSubtype;
        }
        catch (Exception ex)
        {
            // Defense-in-depth: a malformed raw payload should not
            // prevent dispatch -- we just lose the subtype and treat
            // the event as non-app_mention.
            this.logger.LogDebug(
                ex,
                "Slack inbound pipeline could not re-parse event subtype for idempotency_key={IdempotencyKey}.",
                envelope.IdempotencyKey);
            return null;
        }
    }

    private readonly record struct SlackRetryDispatchResult(bool IsSuccess, bool IsAcknowledged)
    {
        public static SlackRetryDispatchResult Success { get; } = new(true, false);

        public static SlackRetryDispatchResult Acknowledged { get; } = new(false, true);

        public static SlackRetryDispatchResult Failure { get; } = new(false, false);
    }
}
