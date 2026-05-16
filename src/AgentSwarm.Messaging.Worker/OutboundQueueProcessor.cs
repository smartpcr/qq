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
///   <see cref="IOutboundQueue.MarkFailedAsync"/>. The processor
///   does NOT pre-compute the retry-budget verdict here; the queue's
///   own MarkFailed transition increments
///   <see cref="OutboundMessage.AttemptCount"/> and lands the row in
///   <see cref="OutboundMessageStatus.Pending"/> when budget remains
///   (re-enqueued with a backoff <c>NextRetryAt</c>) or
///   <see cref="OutboundMessageStatus.Failed"/> when budget is
///   exhausted. This keeps the two terminal statuses semantically
///   distinct — <c>DeadLettered</c> is reserved for "no retry would
///   ever succeed" (Permanent category, or backpressure DLQ on
///   enqueue), and <c>Failed</c> covers "transient cause, budget
///   exhausted" — so dashboards and recovery sweeps don't have to
///   reconcile two paths for the same condition.
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
///   Any other exception → <see cref="IOutboundQueue.MarkFailedAsync"/>
///   with the message text; the queue's retry logic handles eventual
///   transition to <c>Failed</c> when the budget is exhausted.
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
    private readonly OutboundQueueMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboundQueueProcessor> _logger;

    public OutboundQueueProcessor(
        IServiceScopeFactory scopeFactory,
        IOutboundQueue queue,
        IMessageSender sender,
        IOptions<OutboundQueueOptions> options,
        OutboundQueueMetrics metrics,
        TimeProvider timeProvider,
        ILogger<OutboundQueueProcessor> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value
            ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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

            await ExecuteShutdownSafeBookkeepingAsync(token => _queue
                .MarkSentAsync(message.MessageId, sendResult.TelegramMessageId, token))
                .ConfigureAwait(false);

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
            _logger.LogError(
                ex,
                "OutboundQueueProcessor worker {WorkerId} unexpected send failure for MessageId={MessageId} CorrelationId={CorrelationId} Severity={Severity} SourceType={SourceType} AttemptCount={AttemptCount}.",
                workerId,
                message.MessageId,
                message.CorrelationId,
                message.Severity,
                message.SourceType,
                message.AttemptCount);

            // Unknown exception — let the queue's retry/dead-letter
            // logic decide based on the row's remaining attempt budget.
            // Wrap in the shutdown-safe helper so the bookkeeping
            // write completes even when the host's stoppingToken raced
            // (iter-7 evaluator item 4).
            await ExecuteShutdownSafeBookkeepingAsync(token => _queue
                .MarkFailedAsync(message.MessageId, ex.Message, token))
                .ConfigureAwait(false);
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

            await ExecuteShutdownSafeBookkeepingAsync(token => _queue
                .MarkSentAsync(message.MessageId, ex.TelegramMessageId, token))
                .ConfigureAwait(false);

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
        // r0 reviewer item — unify the budget-exhaustion verdict.
        //
        // The processor previously short-circuited on
        // `attemptsRemaining <= 0` and called `DeadLetterAsync`
        // for ANY TelegramSendFailedException whose retry budget was
        // exhausted, including transient causes. The queue's own
        // `MarkFailedAsync` already performs the SAME budget check
        // internally and transitions the row to
        // <see cref="OutboundMessageStatus.Failed"/> when
        // `nextAttempt >= MaxAttempts` (see PersistentOutboundQueue
        // and InMemoryOutboundQueue MarkFailedAsync). That created
        // two different terminal statuses for the same condition:
        // a `MaxAttempts=1` transient failure landed as
        // `DeadLettered` via the processor's pre-check, while a
        // `MaxAttempts=2` transient failure on its second attempt
        // landed as `Failed` via the queue. Dashboards and recovery
        // sweeps had to reconcile both paths.
        //
        // The new contract is:
        //   * Permanent  → DeadLetterAsync immediately (no retry
        //                  would ever help; reserved for the chat-
        //                  blocked / message-too-long / etc.
        //                  "no-recoverable-by-retry" category).
        //   * everything else (TransientTransport, RateLimitExhausted,
        //                  any future non-Permanent category)
        //                → MarkFailedAsync. The queue lands the row
        //                  in `Pending` with a backoff `NextRetryAt`
        //                  when budget remains, or in `Failed` when
        //                  the budget is exhausted. Single owner,
        //                  single audit trail.
        if (ex.FailureCategory == OutboundFailureCategory.Permanent)
        {
            _logger.LogError(
                ex,
                "OutboundQueueProcessor worker {WorkerId} dead-lettering MessageId={MessageId} CorrelationId={CorrelationId} after {AttemptCount}/{MaxAttempts} attempts — FailureCategory=Permanent DeadLetterPersisted={DeadLetterPersisted}.",
                workerId,
                message.MessageId,
                message.CorrelationId,
                message.AttemptCount + 1,
                message.MaxAttempts,
                ex.DeadLetterPersisted);

            // Iter-2 evaluator item 5 — preserve the failure reason
            // and the category on the dead-letter transition. The
            // reason string is shaped as "<category>: <error message>"
            // so the audit row's ErrorDetail column carries both the
            // canonical OutboundFailureCategory enum value AND the
            // human message body — enough for operator triage without
            // chasing across the dead-letter ledger.
            var reason = $"{ex.FailureCategory}: {BuildErrorDetail(ex)}";
            await ExecuteShutdownSafeBookkeepingAsync(token => _queue
                .DeadLetterAsync(message.MessageId, reason, token))
                .ConfigureAwait(false);
            return;
        }

        // Non-Permanent: defer the retry-budget verdict to the queue.
        // We pre-compute the *expected* next status purely for the log
        // line so operators see whether the row is being re-enqueued
        // or terminated; the queue is still the authority on the
        // actual transition (it observes the live AttemptCount under
        // its own concurrency guard).
        var expectedAttempt = message.AttemptCount + 1;
        var expectedToRetry = expectedAttempt < message.MaxAttempts;
        _logger.LogWarning(
            ex,
            "OutboundQueueProcessor worker {WorkerId} transient send failure for MessageId={MessageId} CorrelationId={CorrelationId}; AttemptCount {Attempt}/{MaxAttempts}, FailureCategory={FailureCategory} — deferring to queue ({ExpectedOutcome}).",
            workerId,
            message.MessageId,
            message.CorrelationId,
            expectedAttempt,
            message.MaxAttempts,
            ex.FailureCategory,
            expectedToRetry ? "Pending (retry)" : "Failed (budget exhausted)");

        await ExecuteShutdownSafeBookkeepingAsync(token => _queue
            .MarkFailedAsync(message.MessageId, BuildErrorDetail(ex), token))
            .ConfigureAwait(false);
    }

    private static string BuildErrorDetail(TelegramSendFailedException ex)
    {
        return $"[{ex.FailureCategory}] {ex.Message}";
    }

    /// <summary>
    /// Stage 4.1 iter-7 evaluator items 2 + 4 — runs a queue-state
    /// bookkeeping write (MarkSent / MarkFailed / DeadLetter) on a
    /// fresh, time-bounded <see cref="CancellationTokenSource"/> that
    /// is independent of the processor's
    /// <see cref="BackgroundService.ExecuteAsync(CancellationToken)"/>
    /// stoppingToken. The post-send / post-exception bookkeeping MUST
    /// land regardless of host shutdown timing — otherwise the row
    /// stays in <see cref="OutboundMessageStatus.Sending"/>, the
    /// recovery sweep re-claims it on the next start, and Telegram
    /// receives a duplicate delivery (for the happy path) or the row
    /// loops permanently without ever decrementing its retry budget
    /// (for the failure paths). The 30 s upper bound prevents a
    /// pathologically slow DB write from hanging shutdown
    /// indefinitely.
    /// </summary>
    private static async Task ExecuteShutdownSafeBookkeepingAsync(
        Func<CancellationToken, Task> bookkeeping)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bookkeeping(cts.Token).ConfigureAwait(false);
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
}
