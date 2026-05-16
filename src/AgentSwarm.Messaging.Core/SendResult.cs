namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Outcome of a single platform send attempt by an
/// <see cref="IMessageSender"/> implementation. Captures the success/failure
/// signal plus the platform-side message identifier (when available) so the
/// outbound queue can reconcile the dispatch into
/// <see cref="AgentSwarm.Messaging.Abstractions.OutboundMessageStatus.Sent"/> /
/// <see cref="AgentSwarm.Messaging.Abstractions.OutboundMessageStatus.Failed"/>.
/// See architecture.md Section 4.9.
/// </summary>
/// <param name="Success">
/// <see langword="true"/> when the send completed and the platform accepted
/// the message; <see langword="false"/> for any error (transport failure,
/// rate-limit rejection, validation failure).
/// </param>
/// <param name="PlatformMessageId">
/// Platform-native id of the delivered message (Discord message snowflake cast
/// to <see cref="long"/>). Populated only when <see cref="Success"/> is
/// <see langword="true"/>; <see langword="null"/> on failure or when the
/// platform did not return one (rare).
/// </param>
/// <param name="ErrorMessage">
/// Human-readable failure reason. Required when <see cref="Success"/> is
/// <see langword="false"/>; ignored when it is <see langword="true"/>.
/// </param>
public sealed record SendResult(
    bool Success,
    long? PlatformMessageId,
    string? ErrorMessage)
{
    /// <summary>
    /// Convenience factory for a successful send with the platform-side id.
    /// </summary>
    /// <param name="platformMessageId">Platform identifier returned by the API.</param>
    public static SendResult Succeeded(long platformMessageId) =>
        new(Success: true, PlatformMessageId: platformMessageId, ErrorMessage: null);

    /// <summary>
    /// Convenience factory for a failed send.
    /// </summary>
    /// <param name="errorMessage">Human-readable failure reason. Must be non-empty.</param>
    /// <exception cref="ArgumentException">When <paramref name="errorMessage"/> is null/empty/whitespace.</exception>
    public static SendResult Failed(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException(
                "errorMessage must not be null, empty, or whitespace for a failed SendResult.",
                nameof(errorMessage));
        }

        return new SendResult(Success: false, PlatformMessageId: null, ErrorMessage: errorMessage);
    }
}
