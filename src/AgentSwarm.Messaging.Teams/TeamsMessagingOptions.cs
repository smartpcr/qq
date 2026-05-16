using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Strongly typed configuration for the Teams connector. Bound from <c>appsettings.json</c>
/// and environment variables via the Options pattern in Stage 2.1's <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Field set aligned with <c>implementation-plan.md</c> §2.1 step 5. The Teams-specific
/// retry parameters (<see cref="MaxRetryAttempts"/> = 5 total attempts and
/// <see cref="RetryBaseDelaySeconds"/> = 2-second base delay) implement the canonical Teams
/// retry policy from <c>tech-spec.md</c> §4.4 and override the generic <see cref="ConnectorOptions"/>
/// defaults via the <see cref="ConnectorOptions.RetryCount"/> / <see cref="ConnectorOptions.RetryDelayMs"/> setters.
/// </para>
/// <para>
/// <see cref="ConnectorOptions.RetryCount"/> is documented as "additional attempts after the
/// first attempt fails" (total attempts = <c>RetryCount + 1</c>). The mapping below preserves
/// that semantic: a Teams policy of 5 total attempts (1 initial + 4 retries) yields
/// <c>RetryCount = 4</c>, not 5. Operators reading <see cref="MaxRetryAttempts"/> see the
/// canonical Teams number; the base-class consumer sees the matching retry count.
/// </para>
/// </remarks>
public sealed class TeamsMessagingOptions : ConnectorOptions
{
    private int _maxRetryAttempts = 5;
    private int _retryBaseDelaySeconds = 2;

    /// <summary>
    /// Default constructor. Pre-populates the base <see cref="ConnectorOptions"/> retry
    /// fields with the Teams-specific canonical values (5 total attempts, 2-second base
    /// delay) so that consumers reading the base contract observe the Teams policy without
    /// having to re-derive it.
    /// </summary>
    public TeamsMessagingOptions()
    {
        ApplyRetryDerivedDefaults();
    }

    /// <summary>Bot Framework AAD application ID. Required.</summary>
    public string MicrosoftAppId { get; set; } = string.Empty;

    /// <summary>Bot Framework AAD application secret. Required.</summary>
    public string MicrosoftAppPassword { get; set; } = string.Empty;

    /// <summary>The bot's home tenant (single-tenant configuration). Optional for multi-tenant deployments.</summary>
    public string MicrosoftAppTenantId { get; set; } = string.Empty;

    /// <summary>Tenants whose inbound activities are accepted. Empty list means deny all.</summary>
    public IList<string> AllowedTenantIds { get; set; } = new List<string>();

    /// <summary>Public HTTPS endpoint where Teams posts inbound activities (e.g., <c>https://bot.contoso.com/api/messages</c>).</summary>
    public string BotEndpoint { get; set; } = string.Empty;

    /// <summary>Per-tenant inbound rate limit applied by <c>RateLimitMiddleware</c>. Default 100.</summary>
    public int RateLimitPerTenantPerMinute { get; set; } = 100;

    /// <summary>TTL for the inbound activity-deduplication cache. Default 10 minutes.</summary>
    public int DeduplicationTtlMinutes { get; set; } = 10;

    /// <summary>
    /// Cadence (in seconds) at which <c>QuestionExpiryProcessor</c> scans
    /// <c>IAgentQuestionStore</c> for open questions whose <c>ExpiresAt</c> deadline has
    /// elapsed. Default 60 (per implementation-plan §3.3 step 6). Setting a non-positive
    /// value disables the periodic scan when interpreted by the processor.
    /// </summary>
    public int ExpiryScanIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of expired questions read from the store per scan. Default 50 (per
    /// implementation-plan §3.3 step 6). Larger batches reduce database round-trips at
    /// the cost of holding lock-equivalent state on the store for longer.
    /// </summary>
    public int ExpiryBatchSize { get; set; } = 50;

    /// <summary>
    /// Total number of delivery attempts (1 initial + retries) for outbound Bot Framework
    /// calls, per <c>tech-spec.md</c> §4.4. Default 5. Setting this also recomputes
    /// <see cref="ConnectorOptions.RetryCount"/> to <c>value - 1</c> so the base contract
    /// remains consistent.
    /// </summary>
    public int MaxRetryAttempts
    {
        get => _maxRetryAttempts;
        set
        {
            _maxRetryAttempts = value;
            RetryCount = Math.Max(0, value - 1);
        }
    }

    /// <summary>
    /// Base delay (in seconds) for exponential-backoff retries per <c>tech-spec.md</c> §4.4.
    /// Default 2. Setting this also updates <see cref="ConnectorOptions.RetryDelayMs"/>.
    /// </summary>
    public int RetryBaseDelaySeconds
    {
        get => _retryBaseDelaySeconds;
        set
        {
            _retryBaseDelaySeconds = value;
            RetryDelayMs = value * 1000;
        }
    }

    private void ApplyRetryDerivedDefaults()
    {
        RetryCount = Math.Max(0, _maxRetryAttempts - 1);
        RetryDelayMs = _retryBaseDelaySeconds * 1000;
    }
}
