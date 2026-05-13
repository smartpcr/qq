namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Out-of-band notification channel used to surface dead-letter and
/// critical-failure events to operators on a secondary medium (email,
/// PagerDuty, Slack, second Telegram bot — implementation-defined).
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>AgentSwarm.Messaging.Abstractions</c> per
/// implementation-plan.md Stage 1.4 line 97. The interface uses only
/// primitive parameters so it is genuinely Abstractions-safe (no Core
/// type references), matching the layering rule that Abstractions does
/// not reference Core.
/// </para>
/// <para>
/// Called from Stage 4.1 (<c>OutboundQueueProcessor</c>): when an
/// outbound <c>OutboundMessage</c> exhausts its <c>MaxAttempts</c> and
/// is moved to the dead-letter queue, the processor invokes
/// <see cref="SendAlertAsync"/> so the on-call operator is notified
/// out-of-band rather than silently relying on the same outbound
/// channel that just failed (avoiding the "alert about Telegram
/// failure sent through Telegram" loop).
/// </para>
/// </remarks>
public interface IAlertService
{
    /// <summary>
    /// Notify operators of a critical failure or dead-letter event.
    /// </summary>
    /// <param name="subject">
    /// Short, human-readable headline (e.g. <c>"Outbound message
    /// dead-lettered after 5 attempts"</c>). Must not be null.
    /// </param>
    /// <param name="detail">
    /// Detailed body — typically includes <c>OutboundMessage.MessageId</c>,
    /// <c>CorrelationId</c>, the failure reason, and the latest
    /// <c>ErrorDetail</c> from the outbound row, so the responder can
    /// pivot directly into logs / traces. Must not be null.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAlertAsync(string subject, string detail, CancellationToken ct);
}
