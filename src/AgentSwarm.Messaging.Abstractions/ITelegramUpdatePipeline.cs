namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Structured outcome of <see cref="ITelegramUpdatePipeline.ProcessAsync"/>.
/// </summary>
/// <remarks>
/// <para><see cref="Handled"/> is <c>true</c> when the pipeline fully
/// processed the event, including duplicate short-circuits and unauthorized
/// rejections (those are "handled" by the dedup / authorization stages).</para>
/// <para><see cref="Handled"/> is <c>false</c> only when the event type is
/// unrecognized or the pipeline cannot determine how to process it.</para>
/// </remarks>
public sealed record PipelineResult
{
    private readonly string _correlationId = null!;

    public required bool Handled { get; init; }

    /// <summary>Optional reply text to enqueue back to the operator.</summary>
    public string? ResponseText { get; init; }

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
