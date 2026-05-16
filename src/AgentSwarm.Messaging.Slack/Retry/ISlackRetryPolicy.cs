namespace AgentSwarm.Messaging.Slack.Retry;

/// <summary>
/// Decision surface for retrying failed Slack pipeline work. Consumed by
/// the <c>SlackInboundIngestor</c> (Stage 3.3) when handler processing
/// throws, and by the <c>SlackOutboundDispatcher</c> (Stage 4.1) when a
/// Slack Web API call fails (transient HTTP 5xx, network error, or
/// HTTP 429 rate limit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 1.3 contract.</b> The Slack project ships its own retry
/// interface so that envelope handlers and the outbound dispatcher have a
/// single replaceable knob. In production the implementation will be
/// supplied by the upstream <c>AgentSwarm.Messaging.Core</c> project's
/// generic retry engine (architecture.md section 2.15.4 names
/// <see cref="ShouldRetry"/> and <see cref="GetDelay"/> as the dispatch
/// hooks) per Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </para>
/// <para>
/// Implementations MUST be safe to call concurrently from multiple
/// dispatcher / ingestor workers and SHOULD be stateless so a single
/// instance can be registered as a singleton.
/// </para>
/// </remarks>
internal interface ISlackRetryPolicy
{
    /// <summary>
    /// Indicates whether the failed work should be retried.
    /// </summary>
    /// <param name="attemptNumber">
    /// 1-based count of the attempt that just failed. The first failure is
    /// reported as <c>1</c>; an implementation that caps retries at five
    /// total tries returns <c>false</c> once <paramref name="attemptNumber"/>
    /// reaches <c>5</c>.
    /// </param>
    /// <param name="exception">
    /// The exception that ended the failed attempt. Implementations
    /// typically map transient categories (HTTP 5xx, timeout, HTTP 429) to
    /// <c>true</c> and terminal categories (authentication failure, 4xx
    /// other than 429, schema violation) to <c>false</c>.
    /// </param>
    bool ShouldRetry(int attemptNumber, Exception exception);

    /// <summary>
    /// Returns the delay to wait before the next attempt. Called only when
    /// <see cref="ShouldRetry"/> returned <c>true</c> for the same
    /// <paramref name="attemptNumber"/>. Implementations typically apply
    /// exponential backoff with jitter.
    /// </summary>
    /// <param name="attemptNumber">
    /// 1-based count of the attempt that just failed (same value passed to
    /// the preceding <see cref="ShouldRetry"/> call).
    /// </param>
    TimeSpan GetDelay(int attemptNumber);
}
