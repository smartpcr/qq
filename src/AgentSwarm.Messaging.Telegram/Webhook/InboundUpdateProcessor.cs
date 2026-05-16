using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram.Webhook;

/// <summary>
/// Processes a single persisted <see cref="InboundUpdate"/> row:
/// deserializes <see cref="InboundUpdate.RawPayload"/> back into a
/// Telegram <see cref="Update"/>, maps it through
/// <see cref="TelegramUpdateMapper"/>, drives it through
/// <see cref="ITelegramUpdatePipeline.ProcessAsync"/>, and persists the
/// terminal status per the Stage 2.2 hybrid retry contract
/// (throw = retryable, return = terminal).
/// </summary>
/// <remarks>
/// <para>
/// <b>Status transitions.</b> The processor is the canonical writer of
/// the <see cref="InboundUpdate"/> lifecycle after persistence:
/// <list type="bullet">
///   <item><description><b>Entry</b> — transitions
///   <see cref="IdempotencyStatus.Received"/> /
///   <see cref="IdempotencyStatus.Failed"/> to
///   <see cref="IdempotencyStatus.Processing"/> so a parallel sweep
///   does not pick the same row twice.</description></item>
///   <item><description><b>RawPayload deserialization failure</b>
///   (<see cref="JsonException"/> or <c>null</c> deserialized result) —
///   transitions to <see cref="IdempotencyStatus.Failed"/> via
///   <see cref="IInboundUpdateStore.MarkFailedAsync"/> with a
///   diagnostic written to <see cref="InboundUpdate.ErrorDetail"/>.
///   The persisted RawPayload bytes are immutable, so a deserializer
///   failure is deterministic against the row — replaying through the
///   sweep will fail identically every tick. Per the four-status
///   model (architecture.md §3.1), a row whose pipeline was NEVER
///   invoked is semantically Failed, not Completed; "Completed" is
///   reserved for rows whose routed handler ran to a definitive
///   disposition (Succeeded=true or Succeeded=false). The sweep is
///   bounded by the <c>AttemptCount &lt; MaxRetries</c> filter, so a
///   deterministic parse failure naturally exhausts to the
///   <c>inbound_update_exhausted_retries</c> metric path without an
///   unbounded sweep loop. The dedup reservation was never written
///   (pipeline never ran), so leaving the row Failed is safe — there
///   is no stuck-dedup hazard.</description></item>
///   <item><description><b>Pipeline returns Succeeded=true</b> —
///   transitions to <see cref="IdempotencyStatus.Completed"/> with
///   <c>HandlerErrorDetail = null</c>.</description></item>
///   <item><description><b>Pipeline returns Succeeded=false</b> —
///   transitions to <see cref="IdempotencyStatus.Completed"/> with a
///   diagnostic <c>HandlerErrorDetail</c> snapshot of the routed
///   handler's <see cref="CommandResult.ErrorCode"/> /
///   <see cref="CommandResult.ResponseText"/>. The dedup gate already
///   marked the event processed so the sweep MUST NOT replay this
///   handler — <see cref="IdempotencyStatus.Failed"/> would create a
///   permanent stuck state per implementation-plan §187.</description></item>
///   <item><description><b>Pipeline throws (non-OCE)</b> — transitions
///   to <see cref="IdempotencyStatus.Failed"/>, increments
///   <see cref="InboundUpdate.AttemptCount"/>, writes the exception
///   text to <see cref="InboundUpdate.ErrorDetail"/>. The pipeline's
///   release-on-throw guard already cleared the dedup reservation, so
///   the sweep's replay will succeed at <see cref="IDeduplicationService.TryReserveAsync"/>.
///   </description></item>
///   <item><description><b>Pipeline throws OCE</b> — the row is
///   released back to <see cref="IdempotencyStatus.Received"/> via
///   <see cref="IInboundUpdateStore.ReleaseProcessingAsync"/> WITHOUT
///   incrementing <see cref="InboundUpdate.AttemptCount"/> (cancellation
///   is a shutdown signal, not a failure). The next sweep tick or the
///   next process startup picks the row up via the recovery query
///   (architecture.md §4.8: "recoverable includes Received,
///   Processing, and Failed"). Marking Failed here would be wrong
///   because the in-memory dedup state is also lost on restart;
///   marking Completed would lose the work entirely.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>RawPayload deserialization.</b> Uses
/// <see cref="JsonSerializerOptions.Web"/> as the JSON dialect because
/// Telegram emits camelCase fields per the Bot API spec. Telegram.Bot
/// 22.x ships its own <see cref="System.Text.Json"/> conventions; we
/// rely on the package's <c>[JsonConverter]</c> attributes attached to
/// the <see cref="Update"/> graph for enum/discriminator handling.
/// </para>
/// </remarks>
public sealed class InboundUpdateProcessor
{
    private static readonly JsonSerializerOptions UpdateJsonOptions =
        JsonBotAPI.Options;

    private readonly IInboundUpdateStore _store;
    private readonly ITelegramUpdatePipeline _pipeline;
    private readonly ILogger<InboundUpdateProcessor> _logger;

    public InboundUpdateProcessor(
        IInboundUpdateStore store,
        ITelegramUpdatePipeline pipeline,
        ILogger<InboundUpdateProcessor> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Drives <paramref name="row"/> through the pipeline and writes the
    /// terminal <see cref="IdempotencyStatus"/>. Idempotent: replaying
    /// the same row is safe because the pipeline's dedup gate
    /// short-circuits already-processed events, and the store's atomic
    /// <see cref="IInboundUpdateStore.TryMarkProcessingAsync"/> arbitrates
    /// dispatcher-vs-sweep races at the database engine layer.
    /// </summary>
    /// <param name="row">The persisted update row to process.</param>
    /// <param name="correlationId">Trace/correlation id to attach to the
    /// emitted <see cref="MessengerEvent"/>. Sweep callers pass the
    /// recovered correlation id; live webhook callers pass the
    /// request-scoped id.</param>
    /// <param name="ct">Cancellation token. OCE during pipeline execution
    /// releases the row Processing→Received via
    /// <see cref="IInboundUpdateStore.ReleaseProcessingAsync"/> WITHOUT
    /// bumping <see cref="InboundUpdate.AttemptCount"/> and rethrows so
    /// the dispatcher/sweep loop observes the cancellation. The next
    /// sweep tick replays the row.</param>
    /// <returns><c>true</c> when the pipeline was actually invoked
    /// (regardless of success/failure outcome); <c>false</c> when the
    /// processor short-circuited because
    /// <see cref="IInboundUpdateStore.TryMarkProcessingAsync"/> rejected
    /// the claim (another worker already owns the row). Sweep callers
    /// use this to count only rows they truly drove through the
    /// pipeline rather than rows another worker raced them on.</returns>
    public async Task<bool> ProcessAsync(InboundUpdate row, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "correlationId must be non-null and non-whitespace.", nameof(correlationId));
        }

        // Atomic claim FIRST. Lost race → another worker owns this row;
        // skip silently. This also closes the window where the dispatcher
        // and the sweep both observe Received state and both invoke the
        // pipeline.
        var claimed = await _store.TryMarkProcessingAsync(row.UpdateId, ct).ConfigureAwait(false);
        if (!claimed)
        {
            _logger.LogDebug(
                "TryMarkProcessingAsync lost race; another worker is handling this row. UpdateId={UpdateId} CorrelationId={CorrelationId}",
                row.UpdateId,
                correlationId);
            return false;
        }

        Update? update;
        try
        {
            update = JsonSerializer.Deserialize<Update>(row.RawPayload, UpdateJsonOptions);
        }
        catch (JsonException ex)
        {
            // Deserialization is deterministic against the persisted
            // RawPayload bytes — a sweep replay would fail identically
            // every tick. Per the IInboundUpdateStore four-status model
            // (architecture.md §3.1), a row that never produced a valid
            // MessengerEvent — i.e. the pipeline was never invoked — is
            // semantically Failed, not Completed. "Completed" is reserved
            // for rows whose handler ran to a definitive disposition
            // (Succeeded=true or Succeeded=false); collapsing a parse
            // error into Completed would mask schema regressions in the
            // audit log. The sweep is bounded by the
            // AttemptCount < MaxRetries filter — a deterministic parse
            // failure that returns true here STILL increments AttemptCount
            // (the dispatcher / sweep tracks attempts on the row), so
            // exhausted retries land in the metrics path
            // (inbound_update_exhausted_retries) without an unbounded
            // sweep loop. The dedup reservation was never written
            // (pipeline never ran), so leaving the row Failed is safe —
            // there is no stuck-dedup hazard.
            _logger.LogError(
                ex,
                "Failed to deserialize InboundUpdate.RawPayload — marking InboundUpdate Failed (parse error, pipeline never invoked). UpdateId={UpdateId} CorrelationId={CorrelationId}",
                row.UpdateId,
                correlationId);
            await _store.MarkFailedAsync(
                row.UpdateId,
                "RawPayload deserialization failed: " + ex.Message,
                ct).ConfigureAwait(false);
            return true;
        }

        if (update is null)
        {
            // Same rationale as the JsonException branch — the
            // deserializer's null result is deterministic against the
            // persisted RawPayload bytes, the pipeline was never
            // invoked, so the row's terminal state is Failed.
            _logger.LogError(
                "InboundUpdate.RawPayload deserialized to null — marking InboundUpdate Failed (deserialized to null, pipeline never invoked). UpdateId={UpdateId} CorrelationId={CorrelationId}",
                row.UpdateId,
                correlationId);
            await _store.MarkFailedAsync(
                row.UpdateId,
                "RawPayload deserialization failed: result was null",
                ct).ConfigureAwait(false);
            return true;
        }

        var messengerEvent = TelegramUpdateMapper.Map(update, correlationId, row.ReceivedAt);

        PipelineResult result;
        try
        {
            result = await _pipeline.ProcessAsync(messengerEvent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Release the row Processing→Received WITHOUT bumping
            // AttemptCount — cancellation is a shutdown signal, not a
            // pipeline failure. Architecture.md §4.8 mandates that
            // Processing rows must not be stranded; without this
            // release the row would remain in `Processing` until the
            // next process startup runs `InboundUpdateRecoveryStartup.
            // ResetInterruptedAsync`. Use CancellationToken.None
            // because the inbound `ct` is already cancelled — the
            // release must NOT itself be cancelled. The
            // `ReleaseProcessingAsync` CAS rejects rows already
            // advanced to Completed/Failed by another worker, so a
            // late cancel observed on a finished row is harmless.
            try
            {
                await _store.ReleaseProcessingAsync(row.UpdateId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(
                    releaseEx,
                    "ReleaseProcessingAsync failed while handling cancellation; row may remain in Processing until the next startup recovery sweep. UpdateId={UpdateId} CorrelationId={CorrelationId}",
                    row.UpdateId,
                    correlationId);
            }
            _logger.LogInformation(
                "Pipeline cancelled during processing; row released Processing→Received (AttemptCount unchanged) for sweep recovery. UpdateId={UpdateId} CorrelationId={CorrelationId}",
                row.UpdateId,
                correlationId);
            throw;
        }
        catch (Exception ex)
        {
            // Uncaught pipeline exception is the canonical "retryable"
            // signal — Stage 2.2 brief Scenario 4. The pipeline's release-
            // on-throw guard cleared the dedup reservation (or the
            // exception happened before TryReserveAsync, in which case
            // there is no reservation to clear), so the sweep's replay
            // will succeed at the dedup gate.
            _logger.LogError(
                ex,
                "Pipeline threw — marking InboundUpdate Failed for sweep retry. UpdateId={UpdateId} CorrelationId={CorrelationId}",
                row.UpdateId,
                correlationId);
            await _store.MarkFailedAsync(
                row.UpdateId,
                ex.GetType().Name + ": " + ex.Message,
                ct).ConfigureAwait(false);
            return true;
        }

        if (!result.Succeeded)
        {
            var detail = BuildHandlerErrorDetail(result);
            _logger.LogWarning(
                "Pipeline returned Succeeded=false — marking InboundUpdate Completed with HandlerErrorDetail. UpdateId={UpdateId} CorrelationId={CorrelationId} ErrorCode={ErrorCode}",
                row.UpdateId,
                correlationId,
                result.ErrorCode);
            await _store.MarkCompletedAsync(row.UpdateId, detail, ct).ConfigureAwait(false);
            return true;
        }

        await _store.MarkCompletedAsync(row.UpdateId, null, ct).ConfigureAwait(false);
        return true;
    }

    private static string BuildHandlerErrorDetail(PipelineResult result)
    {
        var code = string.IsNullOrEmpty(result.ErrorCode) ? "(none)" : result.ErrorCode;
        var text = string.IsNullOrEmpty(result.ResponseText) ? "(none)" : result.ResponseText;
        return "ErrorCode=" + code + " ResponseText=" + text;
    }
}
