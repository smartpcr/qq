using System.ComponentModel.DataAnnotations;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Strongly typed configuration for the Teams connector. Bound from
/// <c>appsettings.json</c> / environment variables via the Options pattern in
/// <c>AgentSwarm.Messaging.Worker.Program</c>; validated at startup by
/// <see cref="TeamsMessagingOptionsValidator"/>. Aligned with <c>tech-spec.md</c> §4.4.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxRetryAttempts"/> and <see cref="RetryBaseDelaySeconds"/> override the
/// base-class <see cref="ConnectorOptions.RetryCount"/> / <see cref="ConnectorOptions.RetryDelayMs"/>
/// defaults with the Teams-specific canonical values defined by <c>tech-spec.md</c> §4.4
/// (<c>MaxRetryAttempts = 5</c>, <c>RetryBaseDelaySeconds = 2</c>). The mapping from
/// Teams-specific naming to the base-class fields is performed by
/// <see cref="TeamsMessagingPostConfigure"/>.
/// </para>
/// <para>
/// <see cref="ConnectorOptions.RetryCount"/> represents the TOTAL number of delivery
/// attempts the Teams connector will perform — <see cref="MaxRetryAttempts"/> maps directly
/// onto it without subtracting one. This matches the Teams canonical policy
/// (<c>tech-spec.md</c> §4.4) where the documented value is the total attempt budget.
/// </para>
/// </remarks>
public sealed class TeamsMessagingOptions : ConnectorOptions
{
    /// <summary>Configuration section name used when binding from <c>appsettings.json</c>.</summary>
    public const string SectionName = "TeamsMessaging";

    /// <summary>Microsoft App ID issued by the Bot Framework registration.</summary>
    public string MicrosoftAppId { get; set; } = string.Empty;

    /// <summary>Microsoft App Password (client secret) issued by the Bot Framework registration.</summary>
    public string MicrosoftAppPassword { get; set; } = string.Empty;

    /// <summary>Home tenant for the bot's app registration. Used for Entra-style auth.</summary>
    public string MicrosoftAppTenantId { get; set; } = string.Empty;

    /// <summary>Tenants permitted to send inbound activities. Other tenants are rejected
    /// with HTTP 403 by <c>TenantValidationMiddleware</c>.</summary>
    public IList<string> AllowedTenantIds { get; set; } = new List<string>();

    /// <summary>Public HTTPS endpoint for the bot (e.g., <c>https://bot.example.com/api/messages</c>).</summary>
    public string BotEndpoint { get; set; } = string.Empty;

    /// <summary>Inbound rate-limit ceiling enforced per tenant per minute.</summary>
    [Range(1, int.MaxValue)]
    public int RateLimitPerTenantPerMinute { get; set; } = 100;

    /// <summary>TTL applied by <c>InMemoryActivityIdStore</c> when caching seen activity IDs.</summary>
    [Range(1, int.MaxValue)]
    public int DeduplicationTtlMinutes { get; set; } = 10;

    /// <summary>Total delivery attempts for outbound Teams sends (Teams canonical retry policy).</summary>
    [Range(1, int.MaxValue)]
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Base delay (seconds) between retry attempts before exponential backoff.</summary>
    [Range(1, int.MaxValue)]
    public int RetryBaseDelaySeconds { get; set; } = 2;
}
