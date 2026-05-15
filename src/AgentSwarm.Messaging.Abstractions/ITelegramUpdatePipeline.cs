namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Structured outcome of <see cref="ITelegramUpdatePipeline.ProcessAsync"/>.
/// </summary>
/// <remarks>
/// <para><see cref="Handled"/> is <c>true</c> when the pipeline fully
/// consumed the event, including duplicate short-circuits, unauthorized
/// rejections, and routed handlers that returned a failure (those are
/// "handled" — the pipeline produced a definitive outcome — even though
/// the underlying business action did not succeed).</para>
/// <para><see cref="Handled"/> is <c>false</c> only when the event type is
/// unrecognized or the pipeline cannot determine how to process it.</para>
/// <para><see cref="Succeeded"/> is the orthogonal axis the webhook
/// controller uses to surface the routed handler's outcome to alerting /
/// SIEM consumers without muddling the dedup decision. The Stage 2.4
/// webhook controller maps <see cref="Succeeded"/> to the durable
/// <c>InboundUpdate.IdempotencyStatus</c> column as follows: any normal
/// return — both <c>Succeeded=true</c> AND <c>Succeeded=false</c> — maps
/// to <c>IdempotencyStatus=Completed</c> (the handler ran to completion;
/// the operator has already seen the response; the dedup gate is
/// terminal); only an UNCAUGHT exception thrown out of
/// <see cref="ITelegramUpdatePipeline.ProcessAsync"/> (where the
/// pipeline's catch block called <c>ReleaseReservationAsync</c> first)
/// maps to <c>IdempotencyStatus=Failed</c>, which is the only durable
/// status the <c>InboundRecoverySweep</c> reprocesses. The
/// <c>Succeeded=false</c> handler-return case is recorded with
/// <see cref="ErrorCode"/> / <see cref="ResponseText"/> for audit but
/// does NOT enter the recovery sweep — it is `Completed` at the row
/// level even though the underlying business action did not succeed.
/// Critically: the routed handler returning
/// <see cref="CommandResult.Success"/>=<c>false</c> propagates here as
/// <c>Handled=true / Succeeded=false</c>, but the pipeline DOES call
/// <c>IDeduplicationService.MarkProcessedAsync</c> in that case
/// (and does NOT call <c>IDeduplicationService.ReleaseReservationAsync</c>) —
/// the dedup contract is <i>throw = retryable, return = terminal</i>.
/// A handler that returned a definitive failure response has run to
/// completion and the operator has already seen that response; a live
/// re-delivery hammering the same just-failed handler would surface the
/// same failure repeatedly, so the event is treated as TERMINAL at the
/// dedup gate. Operators who want a retry must re-issue the original
/// command (which produces a fresh <c>EventId</c>). This is intentionally
/// asymmetric with the caught handler-throw path (which DOES release the
/// reservation so a live re-delivery is processed normally per Stage 2.2
/// brief Scenario 4) — see <c>TelegramUpdatePipeline</c> remarks for the
/// full lifecycle table. Pipeline short-circuit replies (unauthorized,
/// role-denied, parse-rejected, duplicate, disambiguation prompt,
/// unknown event type) all set <c>Succeeded=true</c> — they are terminal
/// pipeline-level outcomes, not handler failures, and re-delivery would
/// be pointless.</para>
/// <para><see cref="ErrorCode"/> mirrors
/// <see cref="CommandResult.ErrorCode"/> when the routed handler
/// surfaced one; it is <c>null</c> in every successful path and in
/// pipeline-internal denial paths (which use a fixed
/// <see cref="ResponseText"/> instead of an error code).</para>
/// <para><see cref="ResponseButtons"/> carries an optional inline keyboard
/// the sender layer (Stage 2.3) renders alongside <see cref="ResponseText"/>.
/// Modeling the buttons here (rather than embedding them in
/// <see cref="ResponseText"/>) lets the multi-workspace disambiguation flow
/// surface a real Telegram inline keyboard (per architecture.md §4.3 and
/// e2e-scenarios.md "workspace disambiguation via inline keyboard")
/// without the pipeline needing a Telegram.Bot SDK reference.</para>
/// </remarks>
public sealed record PipelineResult
{
    private readonly string _correlationId = null!;

    public required bool Handled { get; init; }

    /// <summary>
    /// Reflects the routed handler's
    /// <see cref="CommandResult.Success"/> for observability / alerting:
    /// <c>true</c> on the success path, <c>false</c> when the routed
    /// handler returned <see cref="CommandResult.Success"/>=<c>false</c>
    /// (the failure response was still surfaced to the operator and the
    /// event was still marked processed at the dedup gate — see the
    /// type-level remarks for why the dedup contract is symmetric with
    /// success while <c>Succeeded</c> is not). Defaults to <c>true</c> so
    /// existing construction sites that omit the field continue to
    /// reflect the "successful pipeline outcome" semantics they always
    /// represented.
    /// </summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>
    /// Machine-readable error code propagated from
    /// <see cref="CommandResult.ErrorCode"/> when the routed handler
    /// surfaced one; <c>null</c> in every other case (success path,
    /// pipeline-internal denials, duplicate short-circuits, etc.).
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Optional reply text to enqueue back to the operator.</summary>
    public string? ResponseText { get; init; }

    /// <summary>
    /// Optional inline keyboard buttons to render alongside
    /// <see cref="ResponseText"/>. Defaults to an empty list when the
    /// pipeline reply is plain text.
    /// </summary>
    public IReadOnlyList<InlineButton> ResponseButtons { get; init; } = Array.Empty<InlineButton>();

    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}

/// <summary>
/// Inbound processing chain for events mapped from the underlying messenger
/// platform. Both the webhook controller and the polling service map raw
/// platform updates to <see cref="MessengerEvent"/> before invoking
/// <see cref="ProcessAsync"/>, keeping this interface transport-agnostic.
/// </summary>
public interface ITelegramUpdatePipeline
{
    Task<PipelineResult> ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct);
}
