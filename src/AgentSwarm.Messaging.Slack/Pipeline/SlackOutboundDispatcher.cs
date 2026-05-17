// -----------------------------------------------------------------------
// <copyright file="SlackOutboundDispatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Observability;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 6.3 background service: drains
/// <see cref="ISlackOutboundQueue"/>, applies the shared per-tier
/// <see cref="ISlackRateLimiter"/>, dispatches each envelope through
/// <see cref="ISlackOutboundDispatchClient"/>, honours HTTP 429
/// <c>Retry-After</c> by pausing the bucket, retries transient
/// failures per <see cref="ISlackRetryPolicy"/>, dead-letters
/// envelopes that exhaust the retry budget, and writes an outbound
/// <see cref="SlackAuditEntry"/> for every attempt.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 6.3 steps 1, 5, 6, 7, 8. The
/// dispatcher does NOT recreate thread mappings -- it consumes
/// mappings created by the connector via
/// <see cref="ISlackThreadManager"/>. When a dequeued envelope has
/// no mapping (the connector crashed between
/// <see cref="ISlackThreadManager.GetOrCreateThreadAsync"/> and
/// <see cref="ISlackOutboundQueue.EnqueueAsync"/>, or the mapping
/// was deliberately deleted), the envelope is dead-lettered with
/// the reason <c>thread_mapping_missing</c> so an operator can
/// triage rather than silently re-creating a thread the agent did
/// not author.
/// </para>
/// <para>
/// Concurrency: the dispatcher runs a single drain loop. A future
/// stage may shard the loop per-channel if throughput demands it;
/// the in-process channel queue already provides FIFO semantics so
/// a single loop is sufficient for the brief's "10 outbound
/// messages queued for the same channel" scenario.
/// </para>
/// <para>
/// Retry-budget accounting (Stage 6.3 reviewer iter-2 item #1,
/// FR-005 / FR-007 zero-message-loss): rate-limited (HTTP 429)
/// iterations are tracked in a *separate* counter from the
/// configured <c>MaxAttempts</c> budget. Slack explicitly asks us
/// to retry via the <c>Retry-After</c> header and the rate limiter
/// already pauses the bucket for that window, so back-pressure
/// alone must never exhaust the retry budget reserved for genuine
/// transient/permanent failures. A separate, generously sized
/// circuit-breaker guards against a pathological 429 storm (e.g.,
/// a misconfigured scope Slack would never accept) so the loop is
/// still bounded.
/// </para>
/// </remarks>
internal sealed class SlackOutboundDispatcher : BackgroundService
{
    /// <summary><see cref="SlackAuditEntry.Direction"/> for every row this dispatcher writes.</summary>
    public const string DirectionOutbound = "outbound";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for <c>chat.postMessage</c>.</summary>
    public const string RequestTypeMessageSend = "message_send";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for <c>chat.update</c>.</summary>
    public const string RequestTypeMessageUpdate = "message_update";

    /// <summary><see cref="SlackAuditEntry.RequestType"/> for <c>views.update</c>.</summary>
    public const string RequestTypeViewUpdate = "view_update";

    /// <summary><see cref="SlackAuditEntry.Outcome"/> -- success.</summary>
    public const string OutcomeSuccess = "success";

    /// <summary><see cref="SlackAuditEntry.Outcome"/> -- HTTP 429.</summary>
    public const string OutcomeRateLimited = "rate_limited";

    /// <summary><see cref="SlackAuditEntry.Outcome"/> -- transient retryable failure.</summary>
    public const string OutcomeTransient = "transient_failure";

    /// <summary><see cref="SlackAuditEntry.Outcome"/> -- non-retryable Slack error.</summary>
    public const string OutcomePermanent = "permanent_failure";

    /// <summary><see cref="SlackAuditEntry.Outcome"/> -- envelope moved to DLQ.</summary>
    public const string OutcomeDeadLettered = "dead_lettered";

    /// <summary>
    /// <see cref="SlackAuditEntry.Outcome"/> -- secondary failure
    /// recorded when the DLQ enqueue itself fails. The envelope is
    /// NOT dropped from the durable outbound queue when this row is
    /// written; the loop refrains from acknowledging the queue entry
    /// so the message replays on next dequeue / restart, satisfying
    /// FR-005 / FR-007's zero-message-loss constraint.
    /// </summary>
    public const string OutcomeDeadLetterFailed = "dead_letter_failed";

    /// <summary>
    /// Circuit-breaker bound for consecutive HTTP 429 iterations.
    /// Sized far above the typical <c>MaxAttempts</c> budget so
    /// back-pressure cannot exhaust the retry budget reserved for
    /// genuine transient/permanent failures (FR-005 / FR-007), while
    /// still preventing an unbounded loop in pathological cases
    /// (e.g., a misconfigured scope Slack persistently throttles).
    /// The cancellation token always provides the hard escape on
    /// connector shutdown; this bound only matters in steady-state
    /// misconfiguration scenarios. A future iteration may surface
    /// this as a configurable option on <see cref="SlackRetryOptions"/>
    /// if operators need to tune it.
    /// </summary>
    internal const int MaxRateLimitedIterations = 50;

    private readonly ISlackOutboundQueue queue;
    private readonly ISlackThreadManager threadManager;
    private readonly ISlackOutboundDispatchClient dispatchClient;
    private readonly ISlackRateLimiter rateLimiter;
    private readonly ISlackRetryPolicy retryPolicy;
    private readonly ISlackDeadLetterQueue deadLetterQueue;
    private readonly ISlackAuditEntryWriter auditWriter;
    private readonly IOptionsMonitor<SlackConnectorOptions> optionsMonitor;
    private readonly ILogger<SlackOutboundDispatcher> logger;
    private readonly TimeProvider timeProvider;

    public SlackOutboundDispatcher(
        ISlackOutboundQueue queue,
        ISlackThreadManager threadManager,
        ISlackOutboundDispatchClient dispatchClient,
        ISlackRateLimiter rateLimiter,
        ISlackRetryPolicy retryPolicy,
        ISlackDeadLetterQueue deadLetterQueue,
        ISlackAuditEntryWriter auditWriter,
        IOptionsMonitor<SlackConnectorOptions> optionsMonitor,
        ILogger<SlackOutboundDispatcher> logger)
        : this(
            queue,
            threadManager,
            dispatchClient,
            rateLimiter,
            retryPolicy,
            deadLetterQueue,
            auditWriter,
            optionsMonitor,
            logger,
            TimeProvider.System)
    {
    }

    internal SlackOutboundDispatcher(
        ISlackOutboundQueue queue,
        ISlackThreadManager threadManager,
        ISlackOutboundDispatchClient dispatchClient,
        ISlackRateLimiter rateLimiter,
        ISlackRetryPolicy retryPolicy,
        ISlackDeadLetterQueue deadLetterQueue,
        ISlackAuditEntryWriter auditWriter,
        IOptionsMonitor<SlackConnectorOptions> optionsMonitor,
        ILogger<SlackOutboundDispatcher> logger,
        TimeProvider timeProvider)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.threadManager = threadManager ?? throw new ArgumentNullException(nameof(threadManager));
        this.dispatchClient = dispatchClient ?? throw new ArgumentNullException(nameof(dispatchClient));
        this.rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.deadLetterQueue = deadLetterQueue ?? throw new ArgumentNullException(nameof(deadLetterQueue));
        this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.logger.LogInformation("SlackOutboundDispatcher starting.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SlackOutboundEnvelope envelope;
                try
                {
                    envelope = await this.queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                bool dispositionConfirmed = false;
                try
                {
                    await this.DispatchOneAsync(envelope, stoppingToken).ConfigureAwait(false);
                    dispositionConfirmed = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down -- leave the envelope un-acked so the
                    // durable queue re-delivers it on the next start.
                    break;
                }
                catch (SlackOutboundDeadLetterException dlqEx)
                {
                    // The dispatcher's DLQ enqueue itself failed; the
                    // envelope is intentionally NOT acknowledged so the
                    // durable queue replays it. The DeadLetterAsync helper
                    // already wrote a critical log + dead_letter_failed
                    // audit row before throwing.
                    this.logger.LogCritical(
                        dlqEx,
                        "SlackOutboundDispatcher leaving envelope in outbound queue for replay task_id={TaskId} correlation_id={CorrelationId}.",
                        envelope.TaskId,
                        envelope.CorrelationId);
                    // dispositionConfirmed stays false -> no ack -> replay.
                }
                catch (Exception ex)
                {
                    // The DispatchOneAsync catch-blocks already classify
                    // every result; an exception bubbling here is an
                    // unexpected programming error. Capture it on the
                    // audit row + dead-letter so the envelope is not
                    // silently lost, then continue the loop.
                    this.logger.LogError(
                        ex,
                        "SlackOutboundDispatcher unhandled error task_id={TaskId} correlation_id={CorrelationId}.",
                        envelope.TaskId,
                        envelope.CorrelationId);
                    try
                    {
                        await this.DeadLetterAsync(
                            envelope,
                            mapping: null,
                            reason: $"unhandled_exception: {ex.GetType().Name}",
                            exception: ex,
                            attemptCount: 1,
                            firstFailedAt: this.timeProvider.GetUtcNow(),
                            ct: stoppingToken).ConfigureAwait(false);
                        dispositionConfirmed = true;
                    }
                    catch
                    {
                        // intentional swallow: DeadLetterAsync already
                        // wrote the critical log + dead_letter_failed
                        // audit row; leaving dispositionConfirmed=false
                        // keeps the envelope in the durable queue for
                        // replay.
                    }
                }

                if (dispositionConfirmed)
                {
                    await this.TryAcknowledgeAsync(envelope, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            this.logger.LogInformation("SlackOutboundDispatcher stopping.");
        }
    }

    /// <summary>
    /// Acknowledges <paramref name="envelope"/> against the durable
    /// outbound queue (when it implements
    /// <see cref="IAcknowledgeableSlackOutboundQueue"/>) so the
    /// underlying journal can mark the message as fully processed
    /// and skip it on replay. No-op for the in-process channel queue.
    /// Ack failures are logged but never propagate -- the dispositional
    /// audit row is the authoritative success signal.
    /// </summary>
    private async Task TryAcknowledgeAsync(SlackOutboundEnvelope envelope, CancellationToken ct)
    {
        if (this.queue is not IAcknowledgeableSlackOutboundQueue ackable)
        {
            return;
        }

        try
        {
            await ackable.AcknowledgeAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "SlackOutboundDispatcher failed to acknowledge envelope task_id={TaskId} correlation_id={CorrelationId} -- replay-on-restart may re-deliver this message.",
                envelope.TaskId,
                envelope.CorrelationId);
        }
    }

    private async Task DispatchOneAsync(SlackOutboundEnvelope envelope, CancellationToken ct)
    {
        // Stage 7.2: every outbound dispatch produces a single
        // `slack.outbound.send` span wrapping the full retry / rate-
        // limit loop. The span carries the §6.3 attribute set
        // (correlation_id, task_id, team_id, channel_id,
        // operation_kind) and the loop emits per-attempt latency
        // into the `slack.outbound.latency_ms` histogram tagged with
        // the operation kind + final outcome. The counter
        // `slack.outbound.count` is incremented exactly once with
        // the terminal outcome so the dashboard's "calls vs.
        // attempts" ratio stays interpretable. Each HTTP 429
        // observed by the loop bumps `slack.ratelimit.backoff_count`.
        long startTicks = this.timeProvider.GetTimestamp();
        SlackThreadMapping? mapping = await this.threadManager
            .GetThreadAsync(envelope.TaskId, ct)
            .ConfigureAwait(false);

        using Activity? sendSpan = SlackTelemetry.StartOutboundSpan(
            envelope,
            mapping?.TeamId ?? string.Empty,
            mapping?.ChannelId,
            mapping?.AgentId);
        using IDisposable logScope = SlackTelemetry.CreateScope(
            this.logger,
            envelope,
            mapping?.TeamId,
            mapping?.ChannelId,
            mapping?.AgentId);

        if (mapping is null)
        {
            sendSpan?.SetTag(SlackTelemetry.AttributeOutcome, "thread_mapping_missing");
            sendSpan?.SetStatus(ActivityStatusCode.Error, "thread_mapping_missing");
            SlackTelemetry.OutboundCount.Add(
                1,
                new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
                new KeyValuePair<string, object?>(SlackTelemetry.AttributeOutcome, "thread_mapping_missing"),
                new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, string.Empty));
            SlackTelemetry.OutboundLatencyMs.Record(
                this.ElapsedMs(startTicks),
                new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
                new KeyValuePair<string, object?>(SlackTelemetry.AttributeOutcome, "thread_mapping_missing"),
                new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, string.Empty));

            await this.DeadLetterAsync(
                envelope,
                mapping: null,
                reason: "thread_mapping_missing",
                exception: null,
                attemptCount: 0,
                firstFailedAt: this.timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
            return;
        }

        SlackApiTier tier = SlackOutboundTierMap.ForOperation(envelope.MessageType);
        string scopeKey = ResolveScopeKey(this.optionsMonitor.CurrentValue, tier, mapping.TeamId, mapping.ChannelId);
        int maxAttempts = Math.Max(1, this.optionsMonitor.CurrentValue.Retry?.MaxAttempts ?? 5);

        // Stage 6.3 evaluator iter-1 item #1: parse the payload ONCE
        // so chat.update / views.update envelopes can carry their `ts`
        // / `view_id` references through the dispatcher. The frozen
        // primary-constructor surface of SlackOutboundEnvelope keeps
        // the 5 brief-mandated fields, but Stage 6.3 iter 2 added
        // optional init-only MessageTs / ViewId members. We prefer
        // the explicit envelope fields when the producer set them
        // (typed, no parse risk) and fall back to a JSON probe of the
        // pre-rendered payload (legacy producers that embed `ts` /
        // `view_id` directly in the BlockKitPayload). When both
        // sources are silent, the dispatch client surfaces a
        // MissingConfiguration outcome and the failure is
        // dead-lettered with the SlackError detail on the audit row.
        (string? payloadTs, string? payloadViewId) = ExtractUpdateReferences(envelope);
        string? messageTs = envelope.MessageTs ?? payloadTs;
        string? viewId = envelope.ViewId ?? payloadViewId;

        DateTimeOffset? firstFailedAt = null;
        Exception? lastException = null;
        SlackOutboundDispatchResult lastResult = default;
        string lastReason = "exhausted";
        int attempt = 0;
        int actualAttempts = 0;

        // Stage 6.3 reviewer iter-2 item #1 (FR-005 / FR-007
        // zero-message-loss): genuine failure attempts (client
        // exceptions, TransientFailure, PermanentFailure,
        // MissingConfiguration) consume the configured `maxAttempts`
        // budget via `retryBudgetUsed`. HTTP 429 RateLimited
        // iterations DO NOT consume this budget -- Slack explicitly
        // asked us to retry and the rate limiter already paused the
        // bucket for the Retry-After window, so the next
        // AcquireAsync naturally waits. A separate
        // `rateLimitedIterations` counter is bounded by
        // MaxRateLimitedIterations purely as a circuit-breaker for
        // pathological 429 storms (e.g., misconfigured scopes).
        int retryBudgetUsed = 0;
        int rateLimitedIterations = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            actualAttempts = attempt;
            await this.rateLimiter.AcquireAsync(tier, scopeKey, ct).ConfigureAwait(false);

            SlackOutboundDispatchResult result;
            try
            {
                result = await this.dispatchClient.DispatchAsync(
                    new SlackOutboundDispatchRequest(
                        envelope.MessageType,
                        TeamId: mapping.TeamId,
                        ChannelId: mapping.ChannelId,
                        ThreadTs: envelope.ThreadTs ?? mapping.ThreadTs,
                        MessageTs: messageTs,
                        ViewId: viewId,
                        BlockKitPayload: envelope.BlockKitPayload,
                        CorrelationId: envelope.CorrelationId),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                retryBudgetUsed++;
                lastException = ex;
                lastReason = $"client_exception:{ex.GetType().Name}";
                firstFailedAt ??= this.timeProvider.GetUtcNow();
                await this.WriteAuditAsync(
                    envelope,
                    mapping,
                    attempt,
                    outcome: OutcomeTransient,
                    statusCode: 0,
                    errorDetail: lastReason,
                    responsePayload: null,
                    messageTs: null,
                    ct).ConfigureAwait(false);

                if (!this.retryPolicy.ShouldRetry(retryBudgetUsed, ex)
                    || retryBudgetUsed >= maxAttempts)
                {
                    break;
                }

                TimeSpan delay = this.retryPolicy.GetDelay(retryBudgetUsed);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, this.timeProvider, ct).ConfigureAwait(false);
                }

                continue;
            }

            lastResult = result;
            switch (result.Outcome)
            {
                case SlackOutboundDispatchOutcome.Success:
                    await this.WriteAuditAsync(
                        envelope,
                        mapping,
                        attempt,
                        outcome: OutcomeSuccess,
                        statusCode: result.HttpStatusCode,
                        errorDetail: null,
                        responsePayload: result.ResponsePayload,
                        messageTs: result.MessageTs,
                        ct).ConfigureAwait(false);

                    SlackTelemetry.OutboundCount.Add(
                        1,
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeOutcome, OutcomeSuccess),
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, mapping.TeamId));
                    SlackTelemetry.OutboundLatencyMs.Record(
                        this.ElapsedMs(startTicks),
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeOutcome, OutcomeSuccess),
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, mapping.TeamId));
                    sendSpan?.SetTag(SlackTelemetry.AttributeOutcome, OutcomeSuccess);
                    sendSpan?.SetTag("slack.attempts", attempt);
                    sendSpan?.SetStatus(ActivityStatusCode.Ok);
                    return;

                case SlackOutboundDispatchOutcome.RateLimited:
                    rateLimitedIterations++;
                    TimeSpan pause = result.RetryAfter ?? TimeSpan.FromSeconds(1);
                    this.rateLimiter.NotifyRetryAfter(tier, scopeKey, pause);
                    firstFailedAt ??= this.timeProvider.GetUtcNow();
                    lastReason = $"http_429_retry_after_{(int)pause.TotalMilliseconds}ms";

                    // Stage 7.2: every 429 observed bumps the shared
                    // rate-limit back-off counter so dashboards can
                    // alert on persistent throttling per workspace +
                    // operation kind without sampling audit rows.
                    SlackTelemetry.RateLimitBackoffCount.Add(
                        1,
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
                        new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, mapping.TeamId),
                        new KeyValuePair<string, object?>("slack.tier", tier.ToString()));

                    await this.WriteAuditAsync(
                        envelope,
                        mapping,
                        attempt,
                        outcome: OutcomeRateLimited,
                        statusCode: result.HttpStatusCode,
                        errorDetail: lastReason,
                        responsePayload: result.ResponsePayload,
                        messageTs: null,
                        ct).ConfigureAwait(false);

                    // FR-005 / FR-007: rate-limited iterations do NOT
                    // consume the `maxAttempts` retry budget. The
                    // circuit-breaker below caps the loop only in
                    // pathological 429-storm scenarios so a transient
                    // burst cannot dead-letter a message Slack asked
                    // us to retry. When this bound is hit the
                    // envelope IS dead-lettered with a distinct
                    // `rate_limited_circuit_breaker_*` reason so
                    // operators can triage the misconfiguration
                    // separately from genuine transient/permanent
                    // failures.
                    if (rateLimitedIterations >= MaxRateLimitedIterations)
                    {
                        lastReason = $"rate_limited_circuit_breaker_after_{rateLimitedIterations}_iterations";
                        this.logger.LogError(
                            "SlackOutboundDispatcher hit rate-limit circuit breaker task_id={TaskId} correlation_id={CorrelationId} tier={Tier} scope={ScopeKey} consecutive_429s={Count} -- dead-lettering for operator triage; check rate-limit config / Slack workspace scopes.",
                            envelope.TaskId,
                            envelope.CorrelationId,
                            tier,
                            scopeKey,
                            rateLimitedIterations);
                        goto exhausted;
                    }

                    // Don't apply the retry-policy delay -- the rate
                    // limiter already holds the bucket until the
                    // Retry-After window elapses, so the next
                    // AcquireAsync naturally waits.
                    continue;

                case SlackOutboundDispatchOutcome.TransientFailure:
                    retryBudgetUsed++;
                    firstFailedAt ??= this.timeProvider.GetUtcNow();
                    lastReason = $"transient:{result.SlackError ?? "unknown"}";
                    await this.WriteAuditAsync(
                        envelope,
                        mapping,
                        attempt,
                        outcome: OutcomeTransient,
                        statusCode: result.HttpStatusCode,
                        errorDetail: lastReason,
                        responsePayload: result.ResponsePayload,
                        messageTs: null,
                        ct).ConfigureAwait(false);

                    if (!this.retryPolicy.ShouldRetry(retryBudgetUsed, new TransientSlackApiException(lastReason))
                        || retryBudgetUsed >= maxAttempts)
                    {
                        goto exhausted;
                    }

                    TimeSpan tdelay = this.retryPolicy.GetDelay(retryBudgetUsed);
                    if (tdelay > TimeSpan.Zero)
                    {
                        await Task.Delay(tdelay, this.timeProvider, ct).ConfigureAwait(false);
                    }

                    continue;

                case SlackOutboundDispatchOutcome.PermanentFailure:
                    firstFailedAt ??= this.timeProvider.GetUtcNow();
                    lastReason = $"permanent:{result.SlackError ?? "unknown"}";
                    await this.WriteAuditAsync(
                        envelope,
                        mapping,
                        attempt,
                        outcome: OutcomePermanent,
                        statusCode: result.HttpStatusCode,
                        errorDetail: lastReason,
                        responsePayload: result.ResponsePayload,
                        messageTs: null,
                        ct).ConfigureAwait(false);
                    goto exhausted;

                case SlackOutboundDispatchOutcome.MissingConfiguration:
                    firstFailedAt ??= this.timeProvider.GetUtcNow();
                    lastReason = $"missing_configuration:{result.SlackError ?? "unknown"}";
                    await this.WriteAuditAsync(
                        envelope,
                        mapping,
                        attempt,
                        outcome: OutcomePermanent,
                        statusCode: result.HttpStatusCode,
                        errorDetail: lastReason,
                        responsePayload: result.ResponsePayload,
                        messageTs: null,
                        ct).ConfigureAwait(false);
                    goto exhausted;
            }
        }

        // Reached only if `ct` was cancelled between iterations
        // (mid-iteration cancellation surfaces as OCE from the
        // awaits above). Surface it so the outer ExecuteAsync loop
        // observes a cancellation -- which leaves the envelope
        // un-acked for durable replay -- instead of falling through
        // to the dead-letter path.
        ct.ThrowIfCancellationRequested();

exhausted:
        // Stage 6.3 evaluator iter-1 item #5: record the *actual*
        // number of attempts made (1 for a single PermanentFailure or
        // MissingConfiguration), not the configured ceiling. The DLQ
        // / audit row's AttemptCount is operator-facing triage data;
        // an inflated count masks "we gave up immediately" from
        // "we exhausted the budget". `actualAttempts` includes both
        // genuine-failure and rate-limited iterations so an operator
        // triaging the DLQ row sees the full effort spent on the
        // envelope; the per-attempt audit rows distinguish the
        // outcomes.
        SlackTelemetry.OutboundCount.Add(
            1,
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeOutcome, OutcomeDeadLettered),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, mapping?.TeamId ?? string.Empty));
        SlackTelemetry.OutboundLatencyMs.Record(
            this.ElapsedMs(startTicks),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeOperationKind, envelope.MessageType.ToString()),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeOutcome, OutcomeDeadLettered),
            new KeyValuePair<string, object?>(SlackTelemetry.AttributeTeamId, mapping?.TeamId ?? string.Empty));
        sendSpan?.SetTag(SlackTelemetry.AttributeOutcome, OutcomeDeadLettered);
        sendSpan?.SetTag("slack.attempts", Math.Max(1, actualAttempts));
        sendSpan?.SetStatus(ActivityStatusCode.Error, lastReason);

        await this.DeadLetterAsync(
            envelope,
            mapping,
            reason: lastReason,
            exception: lastException ?? (lastResult.SlackError is { } se
                ? new SlackOutboundDispatchException(se)
                : null),
            attemptCount: Math.Max(1, actualAttempts),
            firstFailedAt: firstFailedAt ?? this.timeProvider.GetUtcNow(),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes elapsed wall-clock milliseconds since the supplied
    /// start tick stamp, using the dispatcher's <see cref="TimeProvider"/>
    /// so virtual-clock unit tests can advance the histogram value
    /// deterministically.
    /// </summary>
    private double ElapsedMs(long startTicks)
    {
        TimeSpan elapsed = this.timeProvider.GetElapsedTime(startTicks);
        return elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Extracts the <c>ts</c> / <c>view_id</c> references for a
    /// chat.update or views.update envelope. Prefers the envelope's
    /// optional <see cref="SlackOutboundEnvelope.MessageTs"/> /
    /// <see cref="SlackOutboundEnvelope.ViewId"/> fields when set
    /// (the clean Stage 6.3 iter-2 path); falls back to parsing the
    /// pre-rendered Block Kit payload for backward compatibility with
    /// producers that embed the reference inside the rendered JSON.
    /// </summary>
    /// <remarks>
    /// Returning null when neither source supplies the field lets the
    /// dispatch client's existing validation reject the request with a
    /// <see cref="SlackOutboundDispatchOutcome.MissingConfiguration"/>
    /// outcome, which is then dead-lettered with the SlackError detail
    /// on the audit row.
    /// </remarks>
    internal static (string? MessageTs, string? ViewId) ExtractUpdateReferences(SlackOutboundEnvelope envelope)
    {
        if (envelope.MessageType is not SlackOutboundOperationKind.UpdateMessage
            and not SlackOutboundOperationKind.ViewsUpdate)
        {
            return (null, null);
        }

        // Stage 6.3 iter-2 evaluator item #1: prefer the typed envelope
        // fields when the producer set them. The payload-extraction
        // path below remains as a backward-compat fallback for any
        // producer that still embeds the references inside the
        // rendered Block Kit JSON.
        if (envelope.MessageType == SlackOutboundOperationKind.UpdateMessage
            && !string.IsNullOrEmpty(envelope.MessageTs))
        {
            return (envelope.MessageTs, null);
        }

        if (envelope.MessageType == SlackOutboundOperationKind.ViewsUpdate
            && !string.IsNullOrEmpty(envelope.ViewId))
        {
            return (null, envelope.ViewId);
        }

        if (string.IsNullOrEmpty(envelope.BlockKitPayload))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(envelope.BlockKitPayload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? ts = null;
            string? viewId = null;

            if (envelope.MessageType == SlackOutboundOperationKind.UpdateMessage)
            {
                if (doc.RootElement.TryGetProperty("ts", out JsonElement tsEl)
                    && tsEl.ValueKind == JsonValueKind.String)
                {
                    ts = tsEl.GetString();
                }
                else if (doc.RootElement.TryGetProperty("message_ts", out JsonElement mtsEl)
                    && mtsEl.ValueKind == JsonValueKind.String)
                {
                    ts = mtsEl.GetString();
                }
            }
            else
            {
                if (doc.RootElement.TryGetProperty("view_id", out JsonElement vidEl)
                    && vidEl.ValueKind == JsonValueKind.String)
                {
                    viewId = vidEl.GetString();
                }
                else if (doc.RootElement.TryGetProperty("external_id", out JsonElement extEl)
                    && extEl.ValueKind == JsonValueKind.String)
                {
                    // Slack's views.update accepts either view_id OR
                    // external_id; we forward whichever the producer
                    // supplied via the MessageTs/ViewId slot used by
                    // the dispatch client.
                    viewId = extEl.GetString();
                }
            }

            return (ts, viewId);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task DeadLetterAsync(
        SlackOutboundEnvelope envelope,
        SlackThreadMapping? mapping,
        string reason,
        Exception? exception,
        int attemptCount,
        DateTimeOffset firstFailedAt,
        CancellationToken ct)
    {
        SlackDeadLetterEntry entry = new()
        {
            EntryId = Guid.NewGuid(),
            Source = SlackDeadLetterSource.Outbound,
            Reason = reason,
            ExceptionType = exception?.GetType().FullName,
            AttemptCount = Math.Max(1, attemptCount),
            FirstFailedAt = firstFailedAt,
            DeadLetteredAt = this.timeProvider.GetUtcNow(),
            CorrelationId = envelope.CorrelationId ?? string.Empty,
            Payload = envelope,
        };

        // Stage 6.3 evaluator iter-1 item #4 (no-message-loss): if the
        // DLQ enqueue fails we MUST NOT write a "dead_lettered" audit
        // row or signal success upstream -- the poison message has
        // gone nowhere durable. Instead, write a distinct
        // `dead_letter_failure` audit row so operators see the
        // primary failure AND the secondary DLQ failure, log a
        // critical, and rethrow so the dispatcher's outer loop
        // refrains from acknowledging the queue entry (and the
        // durable outbound queue replays the envelope on the next
        // dequeue / on connector restart).
        try
        {
            await this.deadLetterQueue.EnqueueAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception dlqEx)
        {
            this.logger.LogCritical(
                dlqEx,
                "SlackOutboundDispatcher FAILED TO DEAD-LETTER envelope task_id={TaskId} correlation_id={CorrelationId} reason={Reason} -- envelope retained in outbound queue for replay.",
                envelope.TaskId,
                envelope.CorrelationId,
                reason);

            await this.WriteAuditAsync(
                envelope,
                mapping,
                attemptCount,
                outcome: OutcomeDeadLetterFailed,
                statusCode: 0,
                errorDetail: $"dlq_enqueue_failed:{dlqEx.GetType().Name}:{reason}",
                responsePayload: null,
                messageTs: null,
                ct).ConfigureAwait(false);

            throw new SlackOutboundDeadLetterException(envelope, reason, dlqEx);
        }

        await this.WriteAuditAsync(
            envelope,
            mapping,
            attemptCount,
            outcome: OutcomeDeadLettered,
            statusCode: 0,
            errorDetail: reason,
            responsePayload: null,
            messageTs: null,
            ct).ConfigureAwait(false);

        this.logger.LogWarning(
            "SlackOutboundDispatcher dead-lettered envelope task_id={TaskId} correlation_id={CorrelationId} reason={Reason} attempts={AttemptCount}.",
            envelope.TaskId,
            envelope.CorrelationId,
            reason,
            attemptCount);
    }

    private async Task WriteAuditAsync(
        SlackOutboundEnvelope envelope,
        SlackThreadMapping? mapping,
        int attempt,
        string outcome,
        int statusCode,
        string? errorDetail,
        string? responsePayload,
        string? messageTs,
        CancellationToken ct)
    {
        SlackAuditEntry entry = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = envelope.CorrelationId ?? string.Empty,
            AgentId = mapping?.AgentId,
            TaskId = envelope.TaskId,
            ConversationId = mapping?.ThreadTs ?? envelope.ThreadTs,
            Direction = DirectionOutbound,
            RequestType = ResolveRequestType(envelope.MessageType),
            TeamId = mapping?.TeamId ?? string.Empty,
            ChannelId = mapping?.ChannelId,
            ThreadTs = mapping?.ThreadTs ?? envelope.ThreadTs,
            MessageTs = messageTs,
            UserId = null,
            CommandText = $"attempt={attempt} op={envelope.MessageType}",
            ResponsePayload = responsePayload,
            Outcome = outcome,
            ErrorDetail = errorDetail,
            Timestamp = this.timeProvider.GetUtcNow(),
        };

        try
        {
            await this.auditWriter.AppendAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Audit failures must not abort dispatch -- the primary
            // signal is the dispatch outcome itself; the audit log is
            // best-effort. Log critically so operators can investigate.
            this.logger.LogCritical(
                ex,
                "SlackOutboundDispatcher failed to write outbound audit row task_id={TaskId} correlation_id={CorrelationId} attempt={Attempt} outcome={Outcome}.",
                envelope.TaskId,
                envelope.CorrelationId,
                attempt,
                outcome);
        }
    }

    private static string ResolveRequestType(SlackOutboundOperationKind operation) => operation switch
    {
        SlackOutboundOperationKind.PostMessage => RequestTypeMessageSend,
        SlackOutboundOperationKind.UpdateMessage => RequestTypeMessageUpdate,
        SlackOutboundOperationKind.ViewsUpdate => RequestTypeViewUpdate,
        _ => RequestTypeMessageSend,
    };

    /// <summary>
    /// Returns the rate-limiter scope key for the supplied
    /// (tier, team, channel). Tier 2 (chat.postMessage) is rated
    /// per-channel; the other tiers are rated per-workspace.
    /// </summary>
    internal static string ResolveScopeKey(
        SlackConnectorOptions options,
        SlackApiTier tier,
        string teamId,
        string channelId)
    {
        SlackRateLimitTier config = SlackOutboundTierMap.ResolveTierConfig(
            options?.RateLimits ?? new SlackRateLimitOptions(),
            tier);

        return config.Scope == SlackRateLimitScope.Channel
            ? $"{teamId}:{channelId}"
            : teamId ?? string.Empty;
    }
}

/// <summary>
/// Marker exception passed to <see cref="ISlackRetryPolicy.ShouldRetry"/>
/// when a Slack-classified transient failure occurs. The retry policy
/// inspects the type to decide whether a generic transient is
/// retryable; specific Slack error strings remain on the
/// <see cref="Exception.Message"/>.
/// </summary>
[Serializable]
internal sealed class TransientSlackApiException : Exception
{
    public TransientSlackApiException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Carries a Slack-reported permanent failure into the
/// dead-letter entry's <c>ExceptionType</c> field so an operator
/// triaging the DLQ row can see the original Slack error class.
/// </summary>
[Serializable]
internal sealed class SlackOutboundDispatchException : Exception
{
    public SlackOutboundDispatchException(string slackError)
        : base($"Slack outbound dispatch terminated by error '{slackError}'.")
    {
        this.SlackError = slackError;
    }

    public string SlackError { get; }
}

/// <summary>
/// Raised when <see cref="SlackOutboundDispatcher"/> exhausts a
/// terminal disposition (transient retries / permanent failure) AND
/// the dead-letter queue enqueue also fails. The outer dispatcher
/// loop catches this to skip the queue acknowledgement, leaving the
/// envelope durably retained for replay on next dequeue / connector
/// restart. Required to satisfy FR-005 / FR-007 zero-message-loss
/// when the DLQ itself is degraded.
/// </summary>
[Serializable]
internal sealed class SlackOutboundDeadLetterException : Exception
{
    public SlackOutboundDeadLetterException(SlackOutboundEnvelope envelope, string reason, Exception inner)
        : base($"Slack outbound dispatcher failed to dead-letter envelope task_id={envelope?.TaskId ?? "<null>"} reason='{reason}'.", inner)
    {
        this.Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        this.Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    public SlackOutboundEnvelope Envelope { get; }

    public string Reason { get; }
}
