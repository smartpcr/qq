namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Common configuration shared by every messenger connector. Concrete connectors (Teams,
/// Slack, Discord, Telegram) derive from this class and add their own platform-specific
/// settings — for example, <c>TeamsMessagingOptions</c> overrides <see cref="RetryCount"/>
/// and <see cref="RetryDelayMs"/> with the Teams-specific canonical retry policy per
/// <c>tech-spec.md</c> §4.4.
/// </summary>
/// <remarks>
/// The base defaults below (<see cref="RetryCount"/> = 3, <see cref="RetryDelayMs"/> = 1000)
/// are the generic starter values mandated by <c>implementation-plan.md</c> §1.2. They are
/// asserted by the <c>Options defaults</c> test scenario for that stage.
/// </remarks>
public class ConnectorOptions
{
    /// <summary>
    /// Maximum number of additional delivery attempts performed after the first attempt
    /// fails (i.e., total attempts equals <c>RetryCount + 1</c>). Default: <c>3</c>.
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds between retry attempts. Concrete connectors typically apply
    /// exponential backoff on top of this base — see <c>tech-spec.md</c> §4.4. Default:
    /// <c>1000</c>.
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum number of outbound deliveries the connector may process concurrently. Default
    /// is <c>1</c> so that the base contract preserves per-destination ordering; concrete
    /// connectors that explicitly partition by destination (or that explicitly do not need
    /// ordering) may raise this in their derived options class.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Number of consecutive failed delivery attempts after which an outbox entry is
    /// dead-lettered rather than retried again. Default <c>5</c> aligns with the Teams
    /// canonical retry policy in <c>tech-spec.md</c> §4.4 (<c>MaxRetryAttempts = 5</c>).
    /// </summary>
    public int DeadLetterThreshold { get; set; } = 5;
}
