using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Configuration for the Microsoft Teams connector. Bound from <c>appsettings.json</c>
/// (section <c>"Teams"</c>) and environment variables via the Options pattern.
/// </summary>
/// <remarks>
/// <para>
/// Inherits the cross-connector defaults from <see cref="ConnectorOptions"/> and overrides
/// <see cref="ConnectorOptions.RetryCount"/> / <see cref="ConnectorOptions.RetryDelayMs"/>
/// with the Teams-specific canonical retry policy per <c>tech-spec.md</c> §4.4 (5 total
/// attempts, base 2 s, exponential backoff). The Teams-specific overrides are mirrored on
/// <see cref="MaxRetryAttempts"/> and <see cref="RetryBaseDelaySeconds"/> so configuration
/// callers can express the policy in human-readable units.
/// </para>
/// <para>
/// Required fields are validated at startup by <see cref="TeamsMessagingOptionsValidator"/>
/// — a missing value triggers <c>OptionsValidationException</c> on the first
/// <see cref="Microsoft.Extensions.Options.IOptions{T}.Value"/> read.
/// </para>
/// </remarks>
public sealed class TeamsMessagingOptions : ConnectorOptions
{
    /// <summary>
    /// The bot's Azure AD app (client) ID. Required.
    /// </summary>
    public string MicrosoftAppId { get; set; } = string.Empty;

    /// <summary>
    /// The bot's Azure AD client secret. Required. Must be sourced from a secure store
    /// (Key Vault, environment variable, secret store CSI driver) — never committed to
    /// source.
    /// </summary>
    public string MicrosoftAppPassword { get; set; } = string.Empty;

    /// <summary>
    /// The bot's Azure AD tenant ID (single-tenant bot registration). Required.
    /// </summary>
    public string MicrosoftAppTenantId { get; set; } = string.Empty;

    /// <summary>
    /// Allow-list of Entra ID tenant IDs permitted to send activities to this bot. Activities
    /// from tenants not on this list are rejected at the
    /// <see cref="Middleware.TenantValidationMiddleware"/> layer with HTTP 403.
    /// </summary>
    public IList<string> AllowedTenantIds { get; set; } = new List<string>();

    /// <summary>
    /// The public bot endpoint URI (e.g., <c>https://bot.example.com/api/messages</c>). Used
    /// for diagnostics and manifest generation.
    /// </summary>
    public string BotEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Per-tenant inbound rate limit (activities per 1-minute window). Default: <c>100</c>.
    /// Enforced by <see cref="Middleware.RateLimitMiddleware"/>; over-limit requests return
    /// HTTP 429.
    /// </summary>
    public int RateLimitPerTenantPerMinute { get; set; } = 100;

    /// <summary>
    /// Duration (in minutes) for which a seen Bot Framework <c>Activity.Id</c> is retained
    /// for deduplication. Default: <c>10</c>. Used by
    /// <see cref="InMemoryActivityIdStore"/> and
    /// <see cref="Middleware.ActivityDeduplicationMiddleware"/>.
    /// </summary>
    public int DeduplicationTtlMinutes { get; set; } = 10;

    /// <summary>
    /// Teams-specific override of <see cref="ConnectorOptions.RetryCount"/>. Default
    /// <c>5</c> per <c>tech-spec.md</c> §4.4. Per the Stage 2.1 canonical mapping in
    /// <see cref="TeamsMessagingPostConfigure"/>, this value is mirrored 1:1 into
    /// <see cref="ConnectorOptions.RetryCount"/> (and into
    /// <see cref="ConnectorOptions.DeadLetterThreshold"/>) after configuration binding
    /// completes — so adjusting <see cref="MaxRetryAttempts"/> via configuration
    /// (<c>Teams:MaxRetryAttempts</c>) automatically propagates into the inherited fields.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Teams-specific override of <see cref="ConnectorOptions.RetryDelayMs"/>. Default
    /// <c>2</c> seconds per <c>tech-spec.md</c> §4.4 (exponential backoff base delay). Mapped
    /// to <see cref="ConnectorOptions.RetryDelayMs"/> by
    /// <see cref="TeamsMessagingPostConfigure"/> after configuration binding.
    /// </summary>
    public int RetryBaseDelaySeconds { get; set; } = 2;
}
