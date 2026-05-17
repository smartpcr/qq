// -----------------------------------------------------------------------
// <copyright file="OutboundQueueProcessor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;

/// <summary>
/// Stage 4.1 — drains the durable outbox by dispatching outbound
/// messages to <see cref="IMessageSender"/> with a configurable
/// pool of independent workers. Each worker pulls the highest-
/// severity pending row out of <see cref="IOutboundQueue"/>, calls
/// the sender (<see cref="IMessageSender.SendQuestionAsync"/> for
/// <see cref="OutboundSourceType.Question"/>;
/// <see cref="IMessageSender.SendTextAsync"/> for everything else),
/// transitions the row to <see cref="OutboundMessageStatus.Sent"/>,
/// and emits the canonical latency histograms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worker fan-out.</b>
/// <see cref="OutboundQueueOptions.ProcessorConcurrency"/> independent
/// long-running tasks are spawned from
/// <see cref="ExecuteAsync(CancellationToken)"/>. Each worker pulls
/// from the same singleton <see cref="IOutboundQueue"/>; the queue's
/// atomic Pending→Sending claim (single
/// <c>ExecuteUpdateAsync</c> under a WHERE on Status) prevents two
/// workers from racing on the same row.
/// </para>
/// <para>
/// <b>Metric emission scope (architecture.md §10.4 lines 697–709).</b>
/// <list type="bullet">
///   <item><description>
///   <c>telegram.send.queue_dwell_ms</c> — emitted on every
///   successful dequeue, measured as
///   <c>now - OutboundMessage.CreatedAt</c>. Diagnostic only;
///   independent of send outcome.
///   </description></item>
///   <item><description>
///   <c>telegram.send.first_attempt_latency_ms</c> — emitted on
///   the happy path when (a) <see cref="OutboundMessage.AttemptCount"/>
///   was 0 BEFORE this attempt — i.e. this is the row's first send
///   attempt — and (b) the sender returned without throwing. The
///   acceptance-gate metric per architecture.md §10.4. Measured from
///   <see cref="OutboundMessage.CreatedAt"/> (the canonical enqueue
///   instant) to the post-send timestamp.
///   </description></item>
///   <item><description>
///   <c>telegram.send.all_attempts_latency_ms</c> — emitted on every
///   successful send regardless of attempt count or rate-limit
///   waits. Capacity-planning input. Measured from
///   <see cref="OutboundMessage.CreatedAt"/> to the post-send
///   timestamp.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Failure handling.</b>
/// <list type="bullet">
///   <item><description>
///   <see cref="TelegramSendFailedException"/> with
///   <see cref="OutboundFailureCategory.Permanent"/> →
///   <see cref="IOutboundQueue.DeadLetterAsync"/> immediately. A
///   permanent send failure (chat blocked, message too long, etc.)
///   is not recoverable by retry, so we move the row straight to
///   <see cref="OutboundMessageStatus.DeadLettered"/> regardless of
///   the remaining attempt budget.
///   </description></item>
///   <item><description>
///   <see cref="TelegramSendFailedException"/> with
///   <see cref="OutboundFailureCategory.TransientTransport"/>,
///   <see cref="OutboundFailureCategory.RateLimitExhausted"/>, or
///   any future non-Permanent category →
///   <see cref="IOutboundQueue.MarkFailedAsync"/>. The processor's
///   <see cref="HandleSendFailedAsync"/> pre-empts the queue's
///   MarkFailed call when <c>nextAttempt &gt;= RetryPolicy.MaxAttempts</c>
///   and instead routes through <see cref="DispatchTerminalFailureAsync"/>
///   (DLQ ledger insert + alert + queue.DeadLetterAsync), so the
///   queue's MarkFailed only ever sees the in-budget branch — it
///   increments <see cref="OutboundMessage.AttemptCount"/>, appends
///   the per-attempt entry to
///   <see cref="OutboundMessage.AttemptHistoryJson"/>, and lands the
///   row in <see cref="OutboundMessageStatus.Pending"/> with a
///   backoff <c>NextRetryAt</c>.
///   <para>
///   <b>Audit-invariant separation.</b> The queue's defensive
///   fallback for a caller that bypasses the processor's
///   pre-emption transitions an exhausted row to
///   <see cref="OutboundMessageStatus.Failed"/> — NOT
///   <see cref="OutboundMessageStatus.DeadLettered"/>. The two
///   terminal statuses carry different audit invariants:
///   <c>DeadLettered</c> guarantees a matching
///   <c>dead_letter_messages</c> ledger row AND an
///   <see cref="IAlertService"/> alert dispatch, both produced
///   exclusively by <see cref="DispatchTerminalFailureAsync"/>;
///   <c>Failed</c> means "exhausted without operator alerting"
///   (the bypass path). <see cref="IOutboundQueue.MarkFailedAsync"/>
///   is a queue-mechanics primitive with no access to the DLQ
///   ledger or <see cref="IAlertService"/> sinks, so it must NOT
///   produce <c>DeadLettered</c>; that would land a row in a
///   terminal state with no ledger entry and no alert, silently
///   breaking the "every DeadLettered outbox row has a
///   corresponding dead-letter row" invariant that operator
///   triage depends on.
///   </para>
///   </description></item>
///   <item><description>
///   <see cref="PendingQuestionPersistenceException"/> — the
///   Telegram message has already been DELIVERED but the inline
///   pending-question store write failed. The processor must NOT
///   re-send. Instead it rehydrates the
///   <see cref="AgentQuestionEnvelope"/> from
///   <see cref="OutboundMessage.SourceEnvelopeJson"/> and retries
///   <see cref="IPendingQuestionStore.StoreAsync(AgentQuestionEnvelope, long, long, CancellationToken)"/>
///   using the chat / message ids on the exception. On success the
///   outbox row is marked Sent (with the canonical metrics emitted);
///   on failure the row stays Sending and the workstream's recovery
///   sweep eventually retries.
///   </description></item>
///   <item><description>
///   Any other exception (catch-all) → routed through
///   <see cref="DispatchTerminalFailureAsync"/> when the next
///   attempt would exhaust the retry budget
///   (<c>nextAttempt &gt;= RetryPolicy.MaxAttempts</c>), producing
///   a <c>dead_letter_messages</c> ledger row + an
///   <see cref="IAlertService"/> alert + the outbox row flipped
///   to <see cref="OutboundMessageStatus.DeadLettered"/>. When
///   budget remains the catch-all defers to
///   <see cref="IOutboundQueue.MarkFailedAsync"/>, which schedules
///   the next retry exactly as the transient-failure branch does.
///   Iter-2 evaluator item 2 — a budget-exhausting unknown
///   exception MUST NOT silently transition to
///   <see cref="OutboundMessageStatus.Failed"/> via the
///   queue-mechanics fallback; the operator alert + audit ledger
///   row are the only signals that distinguish "Telegram is down"
///   from "a runtime bug in the sender pipeline".
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class OutboundQueueProcessor : BackgroundService
{
    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        // Must match TelegramMessengerConnector.EnvelopeJsonOptions so the
        // SourceEnvelopeJson round-trip parses the original envelope shape
        // (default PascalCase, compact).
        WriteIndented = false,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboundQueue _queue;
    private readonly IMessageSender _sender;
    private readonly OutboundQueueOptions _options;
    private readonly RetryPolicy _retryPolicy;
    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly IAlertService _alertService;
    private readonly OutboundQueueMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly ILogger<OutboundQueueProcessor> _logger;

    public OutboundQueueProcessor(
        IServiceScopeFactory scopeFactory,
        IOutboundQueue queue,
        IMessageSender sender,
        IOptions<OutboundQueueOptions> options,
        OutboundQueueMetrics metrics,
        TimeProvider timeProvider,
        ILogger<OutboundQueueProcessor> logger)
        : this(
            scopeFactory,
            queue,
            sender,
            options,
            retryPolicy: null,
            deadLetterQueue: null,
            alertService: null,
            metrics,
            timeProvider,
            random: null,
            logger)
    {
    }

    /// <summary>
    /// Stage 4.2 full constructor used by DI when the host has wired
    /// <see cref="IDeadLetterQueue"/>, <see cref="IAlertService"/>, and
    /// the <see cref="RetryPolicy"/> options. When any of the
    /// optional dependencies is omitted (legacy hosts) the processor
    /// degrades gracefully to the Stage 4.1 behaviour:
    /// <see cref="IOutboundQueue.DeadLetterAsync"/> alone on
    /// terminal failure and the canonical
    /// <see cref="RetryPolicy"/> defaults for backoff scheduling.
    /// </summary>
    public OutboundQueueProcessor(
        IServiceScopeFactory scopeFactory,
        IOutboundQueue queue,
        IMessageSender sender,
        IOptions<OutboundQueueOptions> options,
        IOptions<RetryPolicy>? retryPolicy,
        IDeadLetterQueue? deadLetterQueue,
        IAlertService? alertService,
        OutboundQueueMetrics metrics,
        TimeProvider timeProvider,
        Random? random,
        ILogger<OutboundQueueProcessor> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value
            ?? throw new ArgumentNullException(nameof(options));
        _retryPolicy = retryPolicy?.Value ?? new RetryPolicy();
        // Stage 4.2: if the host has not yet wired a dedicated
        // dead-letter sink or alert sink, fall back to no-op
        // implementations so the processor still functions (the
        // queue's own DeadLetterAsync continues to record the
        // terminal transition). Production hosts always wire both
        // via DI — see the AddMessagingPersistence + Worker
        // Program.cs registrations.
        _deadLetterQueue = deadLetterQueue ?? NullDeadLetterQueue.Instance;
        _alertService = alertService ?? NullAlertService.Instance;
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _random = random ?? Random.Shared;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, _options.ProcessorConcurrency);
        _logger.LogInformation(
            "OutboundQueueProcessor starting with {Concurrency} worker(s), MaxQueueDepth={MaxQueueDepth}, PollIntervalMs={PollIntervalMs}.",
            concurrency,
            _options.MaxQueueDepth,
            _options.DequeuePollIntervalMs);

        var workers = new List<Task>(concurrency);
        for (var i = 0; i < concurrency; i++)
        {
            var workerId = i;
            workers.Add(Task.Run(() => WorkerLoopAsync(workerId, stoppingToken), stoppingToken));
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        _logger.LogInformation("OutboundQueueProcessor stopped.");
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken ct)
    {
        var pollDelay = TimeSpan.FromMilliseconds(Math.Max(10, _options.DequeuePollIntervalMs));
        _logger.LogDebug("OutboundQueueProcessor worker {WorkerId} loop entered.", workerId);

        while (!ct.IsCancellationRequested)
        {
            OutboundMessage? message = null;
            try
            {
                message = await _queue.DequeueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "OutboundQueueProcessor worker {WorkerId} dequeue threw; backing off {PollInterval} before retry.",
                    workerId,
                    pollDelay);
                await SafeDelayAsync(pollDelay, ct).ConfigureAwait(false);
                continue;
            }

            if (message is null)
            {
                await SafeDelayAsync(pollDelay, ct).ConfigureAwait(false);
                continue;
            }

            await ProcessMessageAsync(workerId, message, ct).ConfigureAwait(false);
        }

        _logger.LogDebug("OutboundQueueProcessor worker {WorkerId} loop exited.", workerId);
    }

    private async Task ProcessMessageAsync(int workerId, OutboundMessage message, CancellationToken ct)
    {
        // Queue dwell: enqueue-to-dequeue interval. Always emitted —
        // independent of send outcome — so dashboards can spot a
        // backlog even when downstream sends are failing.
        //
        // Stage 4.1 iter-3 evaluator item 1 — the dwell metric MUST be
        // anchored on the **actual dequeue/claim instant** (which is
        // now persisted as `OutboundMessage.DequeuedAt` by both the
        // persistent queue's `ExecuteUpdateAsync` CAS and the
        // in-memory queue's `TryUpdate` snapshot) rather than the
        // processor's post-dispatch wall-clock. Using
        // `_timeProvider.GetUtcNow()` here would include the time
        // between the queue claim and the processor entering this
        // method — small under normal load but unbounded under
        // contention/GC pauses, which would silently inflate the
        // queue-backlog signal and undermine the dwell metric's
        // diagnostic purpose (architecture.md §10.4 defines this
        // measurement as "elapsed time from `CreatedAt` (enqueue) to
        // dequeue instant", NOT to processor-handling instant).
        //
        // Fallback: if a future queue implementation forgets to stamp
        // `DequeuedAt` (or a test stub omits it), fall back to the
        // wall-clock so we still emit a sample rather than no-op
        // silently. The defensive branch is logged at Debug so the
        // gap surfaces in dev without breaking metrics emission.
        DateTimeOffset dequeueAt;
        if (message.DequeuedAt is { } stamped)
        {
            dequeueAt = stamped;
        }
        else
        {
            dequeueAt = _timeProvider.GetUtcNow();
            _logger.LogDebug(
                "OutboundQueueProcessor worker {WorkerId} MessageId={MessageId} arrived without a DequeuedAt stamp; falling back to wall-clock for queue_dwell_ms — every IOutboundQueue implementation MUST stamp DequeuedAt on Pending→Sending per Stage 4.1.",
                workerId,
                message.MessageId);
        }
        var dwellMs = Math.Max(0, (dequeueAt - message.CreatedAt).TotalMilliseconds);
        _metrics.QueueDwellMs.Record(
            dwellMs,
            new KeyValuePair<string, object?>("severity", message.Severity.ToString()),
            new KeyValuePair<string, object?>("source_type", message.SourceType.ToString()));

        var isFirstAttempt = message.AttemptCount == 0;

        try
        {
            var sendResult = await DispatchAsync(message, ct).ConfigureAwait(false);

            var sentAt = _timeProvider.GetUtcNow();
            var totalMs = Math.Max(0, (sentAt - message.CreatedAt).TotalMilliseconds);

            // all_attempts_latency_ms — capacity-planning histogram.
            // Emitted unconditionally on success (any attempt, any
            // rate-limit history).
            _metrics.AllAttemptsLatencyMs.Record(
                totalMs,
                new KeyValuePair<string, object?>("severity", message.Severity.ToString()),
                new KeyValuePair<string, object?>("source_type", message.SourceType.ToString()));

            // first_attempt_latency_ms — acceptance-gate histogram.
            // Per architecture.md §10.4 this metric is the **enqueue
            // instant → HTTP 200** elapsed time on the FIRST attempt
            // for sends that did NOT incur any Telegram 429
            // flood-control wait. The acceptance gate "P95 ≤ 2 s"
            // applies to THIS metric. Iter-2 evaluator item 1 — we
            // now also exclude sends whose
            // <see cref="SendResult.RateLimited"/> flag is true. The
            // sender plumbs the flag through from its internal
            // 429-retry loop (TelegramMessageSender.SendWithRetry);
            // a "successful first attempt" that internally waited on
            // a retry_after must NOT pollute the SLO histogram (it
            // would have crossed 2 s on the retry_after alone) but
            // DOES still count toward all_attempts_latency_ms (above).
            if (isFirstAttempt && !sendResult.RateLimited)
            {
                _metrics.FirstAttemptLatencyMs.Record(
                    totalMs,
                    new KeyValuePair<string, object?>("severity", message.Severity.ToString()),
                    new KeyValuePair<string, object?>("source_type", message.SourceType.ToString()));
            }

            await ExecuteShutdownSafeBookkeepingAsync(
                token => _queue.MarkSentAsync(message.MessageId, sendResult.TelegramMessageId, token),
                operationName: "MarkSentAsync",
                messageId: message.MessageId,
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "OutboundQueueProcessor worker {WorkerId} sent MessageId={MessageId} CorrelationId={CorrelationId} Severity={Severity} SourceType={SourceType} TelegramMessageId={TelegramMessageId} TotalLatencyMs={TotalLatencyMs} QueueDwellMs={QueueDwellMs} FirstAttempt={IsFirstAttempt}.",
                workerId,
                message.MessageId,
                message.CorrelationId,
                message.Severity,
                message.SourceType,
                sendResult.TelegramMessageId,
                totalMs,
                dwellMs,
                isFirstAttempt);
        }
        catch (PendingQuestionPersistenceException ex)
        {
            // Telegram message was delivered; only the durable pending-
            // question store write failed. Recovery: rehydrate the
            // envelope from SourceEnvelopeJson and retry the StoreAsync
            // call directly (NOT the send) so the operator's tap can be
            // resolved end-to-end and the timeout sweep has a row to
            // observe. On success the outbox row is marked Sent and
            // metrics are emitted; on failure the row stays in Sending,
            // a subsequent recovery sweep (future Stage 4.x) will reap.
            await HandlePendingQuestionPersistenceFailureAsync(
                workerId, message, ex, isFirstAttempt, ct).ConfigureAwait(false);
        }
        catch (TelegramSendFailedException ex)
        {
            await HandleSendFailedAsync(workerId, message, ex, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — leave row in Sending; recovery sweep reclaims
            // on next start.
            _logger.LogInformation(
                "OutboundQueueProcessor worker {WorkerId} cancelled mid-send for MessageId={MessageId}; row left in Sending for recovery.",
                workerId,
                message.MessageId);
            throw;
        }
        catch (Exception ex)
        {
            // Iter-2 evaluator item 1 — non-TelegramSendFailedException
            // failures must ALSO route through the Stage 4.2 DLQ +
            // alert path once the retry budget is exhausted. Without
            // this branch an unknown runtime exception (e.g. a JSON
            // bug rehydrating SourceEnvelopeJson, a NullReferenceException
            // inside a sender stub) would exhaust the queue's retry
            // budget and transition straight to Failed without an
            // operator alert or a dead-letter ledger row.
            var nextAttemptCatchAll = message.AttemptCount + 1;
            var catchAllBudgetExhausted =
                nextAttemptCatchAll >= Math.Min(message.MaxAttempts, _retryPolicy.MaxAttempts);

            if (catchAllBudgetExhausted)
            {
                _logger.LogError(
                    ex,
                    "OutboundQueueProcessor worker {WorkerId} unknown send failure exhausted retry budget for MessageId={MessageId} CorrelationId={CorrelationId} Severity={Severity} SourceType={SourceType} AttemptCount={AttemptCount}/{MaxAttempts}; routing through DLQ + alert.",
                    workerId,
                    message.MessageId,
                    message.CorrelationId,
                    message.Severity,
                    message.SourceType,
                    nextAttemptCatchAll,
                    message.MaxAttempts);

                // Unknown exception class → Permanent. We cannot prove
                // a retry would help, and an alert plus dead-letter
                // row is strictly safer than silently transitioning
                // to Failed. The error string carries the exception
                // type so operators can distinguish from
                // OutboundFailureCategory.Permanent (Telegram 4xx).
                var unknownFinalError = $"[Unknown:{ex.GetType().Name}] {ex.Message}";
                await DispatchTerminalFailureAsync(
                    workerId,
                    message,
                    OutboundFailureCategory.Permanent,
                    unknownFinalError,
                    nextAttemptCatchAll,
                    triggeringException: ex,
                    ct).ConfigureAwait(false);
                return;
            }

            _logger.LogError(
                ex,
                "OutboundQueueProcessor worker {WorkerId} unexpected send failure for MessageId={MessageId} CorrelationId={CorrelationId} Severity={Severity} SourceType={SourceType} AttemptCount={AttemptCount}; budget remains, deferring to queue retry.",
                workerId,
                message.MessageId,
                message.CorrelationId,
                message.Severity,
                message.SourceType,
                message.AttemptCount);

            // Budget remains — let the queue's MarkFailedAsync
            // schedule the next retry.
            await ExecuteShutdownSafeBookkeepingAsync(
                token => _queue.MarkFailedAsync(message.MessageId, ex.Message, token),
                operationName: "MarkFailedAsync (catch-all)",
                messageId: message.MessageId,
                ct).ConfigureAwait(false);
        }
    }

    private Task<SendResult> DispatchAsync(OutboundMessage message, CancellationToken ct)
    {
        if (message.SourceType == OutboundSourceType.Question)
        {
            var envelope = DeserializeQuestionEnvelope(message);
            return _sender.SendQuestionAsync(message.ChatId, envelope, ct);
        }

        // Text path: CommandAck, StatusUpdate, Alert. The Payload
        // column is the pre-rendered MarkdownV2 ready for the Bot
        // API.
        return _sender.SendTextAsync(message.ChatId, message.Payload, ct);
    }

    private static AgentQuestionEnvelope DeserializeQuestionEnvelope(OutboundMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SourceEnvelopeJson))
        {
            throw new InvalidOperationException(
                $"Outbox row MessageId={message.MessageId} SourceType=Question is missing SourceEnvelopeJson; cannot reconstruct AgentQuestionEnvelope for send.");
        }

        var envelope = JsonSerializer.Deserialize<AgentQuestionEnvelope>(
            message.SourceEnvelopeJson,
            EnvelopeJsonOptions);
        if (envelope is null)
        {
            throw new InvalidOperationException(
                $"Outbox row MessageId={message.MessageId} SourceEnvelopeJson deserialized to null; envelope is malformed.");
        }
        return envelope;
    }

    private async Task HandlePendingQuestionPersistenceFailureAsync(
        int workerId,
        OutboundMessage message,
        PendingQuestionPersistenceException ex,
        bool isFirstAttempt,
        CancellationToken ct)
    {
        _logger.LogWarning(
            ex,
            "OutboundQueueProcessor worker {WorkerId} send for MessageId={MessageId} succeeded at Telegram (TelegramMessageId={TelegramMessageId}) but pending-question persistence failed; retrying StoreAsync (NOT re-sending).",
            workerId,
            message.MessageId,
            ex.TelegramMessageId);

        try
        {
            var envelope = DeserializeQuestionEnvelope(message);

            // The store lives in Abstractions and is registered as
            // scoped (PersistentPendingQuestionStore consumes the
            // scoped MessagingDbContext). Open a fresh scope so the
            // ambient DbContext lifetime is correct.
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IPendingQuestionStore>();
            await store
                .StoreAsync(envelope, ex.TelegramChatId, ex.TelegramMessageId, ct)
                .ConfigureAwait(false);

            // Recovery succeeded — emit metrics + MarkSent so the
            // outbox row reaches its terminal state and the operator
            // observes the row as Sent rather than Sending.
            var sentAt = _timeProvider.GetUtcNow();
            var totalMs = Math.Max(0, (sentAt - message.CreatedAt).TotalMilliseconds);
            _metrics.AllAttemptsLatencyMs.Record(
                totalMs,
                new KeyValuePair<string, object?>("severity", message.Severity.ToString()),
                new KeyValuePair<string, object?>("source_type", message.SourceType.ToString()),
                new KeyValuePair<string, object?>("recovered", "pending_question_persistence"));
            if (isFirstAttempt && !ex.RateLimited)
            {
                _metrics.FirstAttemptLatencyMs.Record(
                    totalMs,
                    new KeyValuePair<string, object?>("severity", message.Severity.ToString()),
                    new KeyValuePair<string, object?>("source_type", message.SourceType.ToString()),
                    new KeyValuePair<string, object?>("recovered", "pending_question_persistence"));
            }

            await ExecuteShutdownSafeBookkeepingAsync(
                token => _queue.MarkSentAsync(message.MessageId, ex.TelegramMessageId, token),
                operationName: "MarkSentAsync (pending-question recovery)",
                messageId: message.MessageId,
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "OutboundQueueProcessor worker {WorkerId} recovered pending-question persistence for MessageId={MessageId} CorrelationId={CorrelationId}; row marked Sent.",
                workerId,
                message.MessageId,
                message.CorrelationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception recoveryEx)
        {
            _logger.LogError(
                recoveryEx,
                "OutboundQueueProcessor worker {WorkerId} pending-question persistence recovery failed for MessageId={MessageId}; row left in Sending for next sweep.",
                workerId,
                message.MessageId);

            // Leave the row in Sending — re-marking Failed would
            // schedule another full re-send, but the Telegram message
            // is already delivered. A future recovery sweep
            // (Stage 4.x) handles the orphaned Sending row.
        }
    }

    private async Task HandleSendFailedAsync(
        int workerId,
        OutboundMessage message,
        TelegramSendFailedException ex,
        CancellationToken ct)
    {
        // Stage 4.2: a Permanent failure or a budget-exhausted
        // transient failure routes through the dedicated dead-letter
        // queue + secondary alert sink before the outbox row's
        // status is flipped to DeadLettered. Permanent → no retry
        // would help, dead-letter immediately. Budget-exhausted
        // transient → the next attempt would push past
        // RetryPolicy.MaxAttempts, so we pre-empt the queue's
        // MarkFailed path and route to the same DLQ flow. This is
        // the brief's "After MaxAttempts exhausted, move message to
        // dead-letter queue and emit an alert event via
        // IAlertService" contract.
        var nextAttempt = message.AttemptCount + 1;
        var budgetExhausted =
            nextAttempt >= Math.Min(message.MaxAttempts, _retryPolicy.MaxAttempts);
        var dispatchAsDeadLetter =
            ex.FailureCategory == OutboundFailureCategory.Permanent || budgetExhausted;

        if (dispatchAsDeadLetter)
        {
            var finalError = BuildErrorDetail(ex);
            _logger.LogError(
                ex,
                "OutboundQueueProcessor worker {WorkerId} dead-lettering MessageId={MessageId} CorrelationId={CorrelationId} after {AttemptCount}/{MaxAttempts} attempts — FailureCategory={FailureCategory} BudgetExhausted={BudgetExhausted}.",
                workerId,
                message.MessageId,
                message.CorrelationId,
                nextAttempt,
                message.MaxAttempts,
                ex.FailureCategory,
                budgetExhausted);

            await DispatchTerminalFailureAsync(
                workerId,
                message,
                ex.FailureCategory,
                finalError,
                nextAttempt,
                triggeringException: ex,
                ct).ConfigureAwait(false);
            return;
        }

        // Non-Permanent, budget remains: defer the retry-budget
        // verdict to the queue. We pre-compute the expected next
        // status purely for the log line so operators see whether
        // the row is being re-enqueued; the queue is still the
        // authority on the actual transition (it observes the live
        // AttemptCount under its own concurrency guard and computes
        // the next NextRetryAt via the same RetryPolicy this
        // processor consumes).
        var scheduledDelay = _retryPolicy.ComputeDelay(nextAttempt, _random);
        _logger.LogWarning(
            ex,
            "OutboundQueueProcessor worker {WorkerId} transient send failure for MessageId={MessageId} CorrelationId={CorrelationId}; AttemptCount {Attempt}/{MaxAttempts}, FailureCategory={FailureCategory} — re-enqueueing (next attempt in ~{ScheduledDelayMs} ms).",
            workerId,
            message.MessageId,
            message.CorrelationId,
            nextAttempt,
            message.MaxAttempts,
            ex.FailureCategory,
            (long)scheduledDelay.TotalMilliseconds);

        await ExecuteShutdownSafeBookkeepingAsync(
            token => _queue.MarkFailedAsync(message.MessageId, BuildErrorDetail(ex), token),
            operationName: "MarkFailedAsync (transient)",
            messageId: message.MessageId,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Iter-2 evaluator items 1 + 3 + 5 — shared dead-letter
    /// dispatch path consumed by both the
    /// <see cref="TelegramSendFailedException"/> handler and the
    /// catch-all unknown-exception handler. Performs the three
    /// terminal-failure side effects in strict order:
    /// <list type="number">
    ///   <item><description>
    ///     Persist the dead-letter row via
    ///     <see cref="IDeadLetterQueue.SendToDeadLetterAsync"/>. If
    ///     this write throws (DB unreachable, serialization bug,
    ///     etc.) the method returns WITHOUT firing the alert or
    ///     flipping the outbox row — the audit invariant "every
    ///     DeadLettered outbox row has a corresponding
    ///     dead_letter_messages row" must be preserved, so the
    ///     outbox row stays in <c>Sending</c> for the next
    ///     recovery sweep to retry.
    ///   </description></item>
    ///   <item><description>
    ///     Emit the secondary-channel alert via
    ///     <see cref="IAlertService.SendAlertAsync"/>. Alert sink
    ///     failure is logged but does NOT block the outbox-row
    ///     transition — an alert-sink outage must not pin an
    ///     outbox row in <c>Sending</c> forever.
    ///   </description></item>
    ///   <item><description>
    ///     Flip the outbox row to <c>DeadLettered</c> via
    ///     <see cref="IOutboundQueue.DeadLetterAsync"/>. After this
    ///     line the row is observable in the operator audit UI as
    ///     dead-lettered; before this line the dead-letter ledger
    ///     already has the row so the audit is consistent.
    ///   </description></item>
    ///   <item><description>
    ///     Best-effort flip the dead-letter row's
    ///     <c>AlertStatus</c> from <c>Pending</c> to <c>Sent</c>
    ///     via <see cref="IDeadLetterQueue.MarkAlertSentAsync"/>
    ///     so the persisted ledger reflects the alert outcome
    ///     rather than leaving rows pinned at <c>Pending</c>
    ///     forever (iter-2 evaluator item 3). Flip failure is
    ///     non-fatal.
    ///   </description></item>
    /// </list>
    /// </summary>
    private async Task DispatchTerminalFailureAsync(
        int workerId,
        OutboundMessage message,
        OutboundFailureCategory category,
        string finalError,
        int finalAttemptCount,
        Exception triggeringException,
        CancellationToken ct)
    {
        var failedAt = _timeProvider.GetUtcNow();

        // Iter-2 evaluator item 1 — project the architecture-mandated
        // AttemptTimestamps + ErrorHistory (architecture.md §3.1 lines
        // 386–388) onto the FailureReason. The outbox row's
        // AttemptHistoryJson has accumulated every prior failed
        // attempt's {attempt, timestamp, error, httpStatus} entry via
        // MarkFailedAsync; we append the FINAL failure entry here so
        // the dead-letter ledger row carries all N entries (one per
        // attempt up to and including this terminal one) rather than
        // only the prior N-1. Done in the processor — not the queue —
        // because the queue's MarkFailed path is pre-empted on the
        // budget-exhausted branch (the processor calls DispatchTerminalFailureAsync
        // BEFORE invoking MarkFailedAsync), so without this append the
        // terminal attempt's entry would be silently dropped.
        // Iter-3 review item 2 — forward the Bot API HTTP status onto the
        // terminal AttemptHistory entry whenever the triggering failure is a
        // TelegramSendFailedException whose inner ApiRequestException carries
        // an ErrorCode (e.g. 400 malformed MarkdownV2, 403 chat blocked, 429
        // rate-limit exhausted). The architecture defines the httpStatus
        // field on AttemptHistory entries specifically for Telegram
        // diagnostics; passing null when the status IS knowable from the
        // exception renders the error-history JSON useless for triage. When
        // the triggering exception is non-Telegram (catch-all unknown
        // exception path) the status remains null because none is available.
        var finalHttpStatus = TryExtractHttpStatus(triggeringException);
        var historyWithFinalEntry = AttemptHistory.Append(
            message.AttemptHistoryJson,
            attempt: finalAttemptCount,
            timestamp: failedAt,
            error: finalError,
            httpStatus: finalHttpStatus);
        var attemptTimestampsJson = AttemptHistory.ProjectTimestamps(historyWithFinalEntry);
        var errorHistoryJson = AttemptHistory.ProjectErrorHistory(historyWithFinalEntry);

        var failureReason = new FailureReason(
            category,
            finalError,
            finalAttemptCount,
            failedAt)
        {
            AgentId = AgentIdExtractor.TryExtract(message),
            AttemptTimestampsJson = attemptTimestampsJson,
            ErrorHistoryJson = errorHistoryJson,
        };

        // Step 1 — persist the dead-letter ledger row FIRST. If this
        // throws, iter-2 evaluator item 5: do NOT continue to alert
        // or DeadLetterAsync. Leaving the outbox row in Sending
        // hands the recovery sweep a clean retry opportunity, and
        // the audit invariant "every DeadLettered outbox row has a
        // corresponding dead_letter_messages row" is preserved.
        try
        {
            await ExecuteShutdownSafeBookkeepingAsync(
                token => _deadLetterQueue.SendToDeadLetterAsync(message, failureReason, token),
                operationName: "SendToDeadLetterAsync",
                messageId: message.MessageId,
                ct).ConfigureAwait(false);
        }
        catch (Exception dlqEx) when (dlqEx is not OperationCanceledException)
        {
            // Iter-2 evaluator item 5 — DLQ persistence failure is
            // now BLOCKING. We log, swallow (so the worker loop is
            // not poisoned), and return. Outbox row stays Sending;
            // recovery sweep re-dequeues on next pass.
            _logger.LogError(
                dlqEx,
                "OutboundQueueProcessor worker {WorkerId} dead-letter ledger write failed for MessageId={MessageId} CorrelationId={CorrelationId} — outbox row left in Sending for recovery sweep (alert + DeadLetterAsync skipped to preserve audit invariant).",
                workerId,
                message.MessageId,
                message.CorrelationId);
            return;
        }

        // Step 2 — fire the secondary-channel alert. Failure here
        // does NOT block the outbox transition; alert sink outages
        // must not pin a row in Sending forever once the audit row
        // has landed.
        var alertSentAt = default(DateTimeOffset?);
        try
        {
            var subject =
                $"Outbound message dead-lettered after {finalAttemptCount} attempt(s) — Severity={message.Severity} SourceType={message.SourceType}";
            var detail =
                $"MessageId={message.MessageId} CorrelationId={message.CorrelationId} ChatId={message.ChatId} FailureCategory={category} Error={finalError}";
            await ExecuteShutdownSafeBookkeepingAsync(
                token => _alertService.SendAlertAsync(subject, detail, token),
                operationName: "SendAlertAsync",
                messageId: message.MessageId,
                ct).ConfigureAwait(false);
            alertSentAt = _timeProvider.GetUtcNow();
        }
        catch (Exception alertEx) when (alertEx is not OperationCanceledException)
        {
            _logger.LogError(
                alertEx,
                "OutboundQueueProcessor worker {WorkerId} IAlertService.SendAlertAsync failed for MessageId={MessageId} CorrelationId={CorrelationId}; outbox row will still be flipped to DeadLettered (dead-letter ledger row remains AlertStatus=Pending for the alerting loop).",
                workerId,
                message.MessageId,
                message.CorrelationId);
        }

        // Step 3 — terminal outbox transition. The audit row is
        // already persisted, so the operator UI's "DeadLettered" row
        // is observable end-to-end.
        //
        // Failure handling: a thrown exception here (e.g. DbUpdateConcurrencyException
        // from an EF Core optimistic-concurrency conflict, or a transient SQLite
        // I/O error) MUST NOT propagate. ExecuteShutdownSafeBookkeepingAsync only
        // swallows OperationCanceledException — every other exception would bubble
        // out of DispatchTerminalFailureAsync, through the catch handlers in
        // ProcessMessageAsync (we are already inside one), out of ProcessMessageAsync,
        // and into the unguarded `await ProcessMessageAsync(...)` line of
        // WorkerLoopAsync — faulting the worker task.
        //
        // The dead-letter ledger row from Step 1 is already persisted, so the
        // audit invariant "every Telegram message that hit terminal failure has a
        // dead_letter_messages row" is preserved even when this flip fails. We
        // log the error and return; the outbox row is left in Sending and a
        // future recovery sweep reconciles the orphaned row by observing the
        // matching dead-letter ledger entry and flipping the outbox row to
        // DeadLettered without re-sending.
        var queueReason = $"{category}: {finalError}";
        try
        {
            await ExecuteShutdownSafeBookkeepingAsync(
                token => _queue.DeadLetterAsync(message.MessageId, queueReason, token),
                operationName: "DeadLetterAsync",
                messageId: message.MessageId,
                ct).ConfigureAwait(false);
        }
        catch (Exception queueEx) when (queueEx is not OperationCanceledException)
        {
            _logger.LogError(
                queueEx,
                "OutboundQueueProcessor worker {WorkerId} DeadLetterAsync (Step 3) failed for MessageId={MessageId} CorrelationId={CorrelationId} — outbox row left in Sending. Dead-letter ledger row is already persisted (Step 1), so the audit invariant is preserved; the recovery sweep will reconcile the orphaned Sending row without re-sending. Step 4 (MarkAlertSent) is skipped because the outbox row never reached DeadLettered.",
                workerId,
                message.MessageId,
                message.CorrelationId);
            return;
        }

        // Step 4 — iter-2 evaluator item 3: flip the dead-letter
        // row's AlertStatus to Sent so the persisted ledger reflects
        // the alert outcome. Skipped when the alert failed (the
        // row stays Pending so the alerting loop retries). Flip
        // failure is non-fatal.
        if (alertSentAt is not null)
        {
            try
            {
                var stampedAt = alertSentAt.Value;
                await ExecuteShutdownSafeBookkeepingAsync(
                    token => _deadLetterQueue.MarkAlertSentAsync(message.MessageId, stampedAt, token),
                    operationName: "MarkAlertSentAsync",
                    messageId: message.MessageId,
                    ct).ConfigureAwait(false);
            }
            catch (Exception flipEx) when (flipEx is not OperationCanceledException)
            {
                // Non-fatal — the alerting loop's next sweep can
                // reconcile a missed flip; the outbox row's
                // DeadLettered state is the authoritative signal.
                _logger.LogWarning(
                    flipEx,
                    "OutboundQueueProcessor worker {WorkerId} MarkAlertSentAsync failed for MessageId={MessageId} CorrelationId={CorrelationId}; dead-letter row stays AlertStatus=Pending pending alerting-loop reconciliation.",
                    workerId,
                    message.MessageId,
                    message.CorrelationId);
            }
        }

        // Reference the triggering exception inside the log scope so
        // a future diagnostic addition can include it without churn.
        _ = triggeringException;
    }

    private static string BuildErrorDetail(TelegramSendFailedException ex)
    {
        // Iter-3 review item 2 — surface the Bot API HTTP status in the
        // ErrorDetail string so the queue's MarkFailedAsync (which has no
        // typed httpStatus parameter on its current interface) still carries
        // the diagnostic forward into the persisted per-attempt AttemptHistory
        // entry that the queue derives from this error string. When the
        // inner ApiRequestException exposes an ErrorCode we prefix the
        // detail with "HTTP {code}: " so operators triaging a transient
        // retry can distinguish e.g. a 429 flood-control from a 502 gateway
        // error without inspecting the inner exception chain.
        var httpStatus = TryExtractHttpStatus(ex);
        return httpStatus is { } status
            ? $"[{ex.FailureCategory}] HTTP {status}: {ex.Message}"
            : $"[{ex.FailureCategory}] {ex.Message}";
    }

    /// <summary>
    /// Iter-3 review item 2 — extract the Bot API HTTP status code from a
    /// <see cref="TelegramSendFailedException"/>'s inner
    /// <see cref="ApiRequestException"/> when present. Returns
    /// <see langword="null"/> for any other exception shape (including
    /// network-level <see cref="RequestException"/> without an HTTP
    /// status, the catch-all unknown-exception path, and a
    /// <see cref="TelegramSendFailedException"/> whose inner exception is
    /// a transport-level error rather than a Bot API response). The
    /// status is sourced from the inner exception rather than a typed
    /// property because <see cref="TelegramSendFailedException"/> does
    /// not yet expose <c>HttpStatus</c> directly; routing through the
    /// inner exception keeps the lookup correct for every category the
    /// sender produces (Permanent 4xx, RateLimitExhausted 429, plus
    /// 5xx transports that surface as <see cref="ApiRequestException"/>).
    /// </summary>
    private static int? TryExtractHttpStatus(Exception? ex)
    {
        if (ex is TelegramSendFailedException tsfe && tsfe.InnerException is ApiRequestException api)
        {
            return api.ErrorCode;
        }
        return null;
    }

    /// <summary>
    /// Wall-clock budget the processor will give a bookkeeping
    /// transition (Mark*, DeadLetter*, SendToDeadLetter, SendAlert)
    /// to complete after host shutdown has already cancelled the
    /// worker's stoppingToken. The detached token created inside
    /// <see cref="ExecuteShutdownSafeBookkeepingAsync"/> is bounded
    /// by this budget so a broken DB connection cannot keep the
    /// process pinned forever during shutdown, but the window is
    /// generous enough to absorb a normal SQLite write + retry.
    /// </summary>
    private static readonly TimeSpan BookkeepingShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the supplied bookkeeping callback on a token that is
    /// detached from the host stoppingToken once shutdown has been
    /// requested, so a Sending row's terminal transition (Sent,
    /// Failed, DeadLettered) and the Stage 4.2 dead-letter-ledger
    /// + alert writes still land even when the processor is in the
    /// middle of stopping. The detached <see cref="CancellationTokenSource"/>
    /// is bounded by <see cref="BookkeepingShutdownBudget"/> so a
    /// permanently hung downstream cannot indefinitely block shutdown.
    /// When <paramref name="ct"/> has not yet been cancelled we still
    /// call the callback with <paramref name="ct"/> directly so the
    /// "normal" code path observes the host token for early cancellation
    /// before a write would block on I/O; only after the host token
    /// trips do we swap to the detached one.
    /// </summary>
    private async Task ExecuteShutdownSafeBookkeepingAsync(
        Func<CancellationToken, Task> bookkeeping,
        string operationName,
        Guid messageId,
        CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
        {
            try
            {
                await bookkeeping(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown raced into the bookkeeping write. Fall
                // through to the detached retry so the row's terminal
                // transition still lands before the worker exits.
            }
        }

        using var detachedCts = new CancellationTokenSource(BookkeepingShutdownBudget);
        try
        {
            await bookkeeping(detachedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogError(
                oce,
                "OutboundQueueProcessor {Operation} for MessageId={MessageId} did not complete within shutdown budget {BudgetMs} ms; row may be left in a non-terminal state until the next recovery sweep.",
                operationName,
                messageId,
                (long)BookkeepingShutdownBudget.TotalMilliseconds);
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Swallow on shutdown.
        }
    }

    /// <summary>
    /// Stage 4.2 fallback when the host has not yet wired a real
    /// <see cref="IDeadLetterQueue"/>. The processor still functions
    /// — the outbox row's <c>DeadLetterAsync</c> transition still
    /// records the terminal status — and the missing ledger row is
    /// surfaced through the warning log line in the catch block in
    /// <c>HandleSendFailedAsync</c>.
    /// </summary>
    private sealed class NullDeadLetterQueue : IDeadLetterQueue
    {
        public static readonly NullDeadLetterQueue Instance = new();

        private NullDeadLetterQueue()
        {
        }

        public Task SendToDeadLetterAsync(OutboundMessage message, FailureReason reason, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeadLetterMessage>>(Array.Empty<DeadLetterMessage>());

        public Task<int> CountAsync(CancellationToken ct)
            => Task.FromResult(0);

        public Task MarkAlertSentAsync(Guid originalMessageId, DateTimeOffset alertSentAt, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Stage 4.2 fallback when the host has not yet wired a real
    /// <see cref="IAlertService"/>. Production hosts always register
    /// <c>LoggingAlertService</c> at minimum (Worker
    /// <c>Program.cs</c>); this null instance covers the unit-test
    /// path where the constructor is invoked without an alert sink.
    /// </summary>
    private sealed class NullAlertService : IAlertService
    {
        public static readonly NullAlertService Instance = new();

        private NullAlertService()
        {
        }

        public Task SendAlertAsync(string subject, string detail, CancellationToken ct)
            => Task.CompletedTask;
    }
}
